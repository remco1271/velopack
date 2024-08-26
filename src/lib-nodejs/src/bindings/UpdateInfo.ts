// This file was generated by [ts-rs](https://github.com/Aleph-Alpha/ts-rs). Do not edit this file manually.
import type { VelopackAsset } from "./VelopackAsset";

/**
 * Holds information about the current version and pending updates, such as how many there are, and access to release notes.
 */
export type UpdateInfo = {
  /**
   * The available version that we are updating to.
   */
  TargetFullRelease: VelopackAsset;
  /**
   * True if the update is a version downgrade or lateral move (such as when switching channels to the same version number).
   * In this case, only full updates are allowed, and any local packages on disk newer than the downloaded version will be
   * deleted.
   */
  IsDowngrade: boolean;
};