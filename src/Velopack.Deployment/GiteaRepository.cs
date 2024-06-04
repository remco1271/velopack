using System.Text;
using Microsoft.Extensions.Logging;
using Velopack.NuGet;
using Velopack.Packaging;
using Velopack.Packaging.Exceptions;
using Velopack.Sources;
using Gitea.Net.Client;
using Amazon.S3.Model;
using Gitea.Net.Model;
using Gitea.Net.Api;
using System.Diagnostics;
using System.Net.Mail;
using Attachment = Gitea.Net.Model.Attachment;
using System.IO;

namespace Velopack.Deployment;

public class GiteaDownloadOptions : RepositoryOptions
{
    public bool Prerelease { get; set; }

    public string RepoUrl { get; set; }

    public string Token { get; set; }

    ///// <summary>
    ///// Example https://gitea.com
    ///// </summary>
    //public string ServerUrl { get; set; }

    //public int ServerPort { get; set; }
}

public class GiteaUploadOptions : GiteaDownloadOptions
{
    public bool Publish { get; set; }

    public string ReleaseName { get; set; }

    public string TagName { get; set; }

    public string TargetCommitish { get; set; }

    public bool Merge { get; set; }
}
public class GiteaRepository : SourceRepository<GiteaDownloadOptions, GiteaSource>, IRepositoryCanUpload<GiteaUploadOptions>
{
    public GiteaRepository(ILogger logger) : base(logger)
    {
    }
    public override GiteaSource CreateSource(GiteaDownloadOptions options)
    {
        return new GiteaSource(options.RepoUrl, options.Token, options.Prerelease);
    }

    public static (string owner, string repo) GetOwnerAndRepo(string repoUrl)
    {
        var repoUri = new Uri(repoUrl);
        var repoParts = repoUri.AbsolutePath.Trim('/').Split('/');
        if (repoParts.Length != 2)
            throw new Exception($"Invalid Gitea URL, '{repoUri.AbsolutePath}' should be in the format 'owner/repo'");

        var repoOwner = repoParts[0];
        var repoName = repoParts[1];
        return (repoOwner, repoName);
    }

    public async Task UploadMissingAssetsAsync(GiteaUploadOptions options)
    {
        var (repoOwner, repoName) = GetOwnerAndRepo(options.RepoUrl);
        var helper = new ReleaseEntryHelper(options.ReleaseDir.FullName, options.Channel, Log);
        var build = BuildAssets.Read(options.ReleaseDir.FullName, options.Channel);
        var latest = helper.GetLatestFullRelease();
        var latestPath = Path.Combine(options.ReleaseDir.FullName, latest.FileName);
        var releaseNotes = new ZipPackage(latestPath).ReleaseNotes;
        var semVer = options.TagName ?? latest.Version.ToString();
        var releaseName = string.IsNullOrWhiteSpace(options.ReleaseName) ? semVer.ToString() : options.ReleaseName;

        // Setup Gitea config
        Configuration config = new Configuration();
        // Example: http://www.Gitea.com/api/v1
        var uri = new Uri(options.RepoUrl);
        var baseUri = uri.GetLeftPart(System.UriPartial.Authority);
        config.BasePath = baseUri + "/api/v1";

        Log.Info($"Preparing to upload {build.Files.Count} asset(s) to Gitea");

        // Set token if provided
        if(!string.IsNullOrWhiteSpace(options.Token)) {
            config.ApiKey.Add("token", options.Token);
        }
        var apiInstance = new RepositoryApi(config);
        //var existingReleases = await client.Repository.Release.GetAll(repoOwner, repoName);
        Release existingReleases = null;
        if (!options.Merge) {

            try {
                existingReleases = apiInstance.RepoGetReleaseByTag(repoOwner, repoName, semVer.ToString());
                if (existingReleases != null) 
                {
                    throw new UserInfoException($"There is already an existing release tagged '{semVer}'. Please delete this release or provide a new version number.");
                }
            } 
            catch (Gitea.Net.Client.ApiException ex) 
            {
                // API throws when release is not found
                if (ex.ErrorCode == 404)
                    Log.Debug("Release not found with tag");
                else
                    throw;
            } 
            catch (Exception) 
            {
                throw;
            }
            //if (existingReleases.Any(r => r.Name == releaseName)) {
            //    throw new UserInfoException($"There is already an existing release named '{releaseName}'. Please delete this release or provide a new release name.");
            //}
        }

        // create or retrieve Gitea release
        Release release = new Release();
        //var release = existingReleases.FirstOrDefault(r => r.TagName == semVer.ToString())
        //    ?? existingReleases.FirstOrDefault(r => r.Name == releaseName);

        if (existingReleases != null) {
            if (existingReleases.TagName != semVer.ToString())
                throw new UserInfoException($"Found existing release matched by name ({existingReleases.Name} [{existingReleases.TagName}]), but tag name does not match ({semVer}).");
            Log.Info($"Found existing release ({existingReleases.Name} [{existingReleases.TagName}]). Merge flag is enabled.");
        } else {
            var newReleaseReq = new CreateReleaseOption(semVer.ToString()) {
                Body = releaseNotes,
                Draft = true,
                Prerelease = options.Prerelease,
                Name = string.IsNullOrWhiteSpace(options.ReleaseName) ? semVer.ToString() : options.ReleaseName,
                TargetCommitish = options.TargetCommitish,
            };
            Log.Info($"Creating draft release titled '{newReleaseReq.Name}'");
            release = apiInstance.RepoCreateRelease(repoOwner, repoName, newReleaseReq);
        }

        // check if there is an existing releasesFile to merge
        var releasesFileName = Utility.GetVeloReleaseIndexName(options.Channel);
        var releaseAsset = release.Assets.FirstOrDefault(a => a.Name == releasesFileName);
        if (releaseAsset != null) {
            throw new UserInfoException($"There is already a remote asset named '{releasesFileName}', and merging release files on Gitea is not supported.");
        }

        // upload all assets (incl packages)
        foreach (var a in build.Files) {
            await RetryAsync(() => UploadFileAsAsset(apiInstance, release, repoOwner, repoName, a), $"Uploading asset '{Path.GetFileName(a)}'..");
        }

        var feed = new VelopackAssetFeed {
            Assets = build.GetReleaseEntries().ToArray(),
        };
        var json = ReleaseEntryHelper.GetAssetFeedJson(feed);

        await RetryAsync(() => {
            Attachment response = apiInstance.RepoCreateReleaseAttachment(repoOwner, repoName, release.Id, releasesFileName, new MemoryStream(Encoding.UTF8.GetBytes(json)));
            return Task.FromResult(response);
        }, "Uploading " + releasesFileName);

        if (options.Channel == ReleaseEntryHelper.GetDefaultChannel(RuntimeOs.Windows)) {
            var legacyReleasesContent = ReleaseEntryHelper.GetLegacyMigrationReleaseFeedString(feed);
            var legacyReleasesBytes = Encoding.UTF8.GetBytes(legacyReleasesContent);
            await RetryAsync(() => {
                Attachment response = apiInstance.RepoCreateReleaseAttachment(repoOwner, repoName, release.Id, "RELEASES", new MemoryStream(legacyReleasesBytes));
                return Task.FromResult(response);
            }, "Uploading legacy RELEASES (compatibility)");
        }

        // convert draft to full release
        if (options.Publish) {
            if (release.Draft) {
                Log.Info("Converting draft to full published release.");
                var body = new EditReleaseOption(); // EditReleaseOption? |  (optional)
                body.Draft = false;
                body.Prerelease = release.Prerelease;
                body.Body = release.Body;
                body.Name = release.Name;
                body.TagName = release.TagName;
                body.TargetCommitish = release.TargetCommitish;
                Release result = apiInstance.RepoEditRelease(repoOwner, repoName, release.Id, body);
            } else {
                Log.Info("Skipping publish, release is already not a draft.");
            }
        }
    }

    private Task UploadFileAsAsset(RepositoryApi client, Release release, string repoOwner, string repoName, string filePath)
    {
        using var stream = File.OpenRead(filePath);
        // Create a release attachment
        Attachment response = client.RepoCreateReleaseAttachment(repoOwner, repoName, release.Id, Path.GetFileName(filePath), stream);
        return Task.FromResult(response);
    }
}
