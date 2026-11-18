# Releasing KasuMeta

How a version gets from `pre/kasu_meta` to Jellyfin servers. The release itself is automated
by [`.github/workflows/release.yml`](.github/workflows/release.yml), which runs when a version
tag is pushed. The steps below are the parts a developer does by hand.

Commands use `git` and the GitHub CLI (`gh`, signed in with push access to the repository).
Replace `1.0.0` with the version being released.

## 1. Prepare the version

Work on `pre/kasu_meta`. Release versions have three parts (`1.0.0`); the plugin version adds
`.0` (`1.0.0.0`).

1. Set the plugin version in four places:
   - `Directory.Build.props`: `<Version>1.0.0.0</Version>`.
   - `build.yaml`: `version: "1.0.0.0"`, and rewrite its `changelog`, which is the text
     Jellyfin's plugin catalog shows for this version.
   - `docker/dev/compose.yaml`: `dest=/config/plugins/KasuMeta_1.0.0.0`.
   - `README.md`: the install path `plugins/KasuMeta_1.0.0.0/`.
2. In `CHANGELOG.md`, move the `[Unreleased]` entries into a new section headed
   `## [1.0.0] - YYYY-MM-DD`, and leave an empty `## [Unreleased]` above it. That section
   becomes the GitHub release notes; without one, GitHub generates notes from the commits.
3. Check that everything agrees, including the tag you are about to push:

   ```sh
   bash scripts/check-version.sh v1.0.0
   ```

4. Run the tests, and the live checks against the dev server (see the README):

   ```sh
   dotnet test
   dotnet build -c Release && docker compose -f docker/dev/compose.yaml up -d
   bash scripts/smoke-test.sh && bash scripts/e2e-test.sh
   ```

5. Commit, push `pre/kasu_meta`, and wait for CI to pass:

   ```sh
   git push origin pre/kasu_meta
   gh run watch "$(gh run list --branch pre/kasu_meta --workflow ci.yml --limit 1 --json databaseId --jq '.[0].databaseId')" --exit-status
   ```

## 2. Bring `main` up to date

`main` only ever moves forward to commits already on `pre/kasu_meta`:

```sh
git fetch origin
git checkout main
git merge --ff-only origin/pre/kasu_meta
git push origin main
git checkout pre/kasu_meta
```

If `--ff-only` refuses, `main` has commits that `pre/kasu_meta` lacks. Bring them into
`pre/kasu_meta` first, and never force-push `main`.

## 3. Tag the release

Tag the commit `main` now points at, and push the tag:

```sh
git tag -a v1.0.0 -m "KasuMeta 1.0.0" origin/main
git push origin v1.0.0
```

Pushing the tag starts the release workflow:

1. CI runs again: version check, tests on every Jellyfin 12.x line, and the package.
2. It checks that the tag matches the plugin version.
3. It builds `kasumeta_1.0.0.0.zip` and publishes a GitHub release with it, with the
   changelog section as notes.
4. It adds the version to `manifest.json` on the `gh-pages` branch, creating the branch on
   the first release. The manifest entry points at the zip on the release.

Follow it with:

```sh
gh run watch "$(gh run list --workflow release.yml --limit 1 --json databaseId --jq '.[0].databaseId')" --exit-status
gh release view v1.0.0
```

## 4. Publish the plugin repository (first release only)

GitHub Pages serves `manifest.json` from `gh-pages`. Enable it once, after the first release
has created the branch (GitHub only offers branches that exist):

- **In the browser:** Settings › Pages › Build and deployment › Source "Deploy from a
  branch" › Branch `gh-pages`, folder `/ (root)` › Save.
- **Or with the CLI:**

  ```sh
  gh api -X POST repos/WaterBlueNewWorld/KasuMeta/pages -f "source[branch]=gh-pages" -f "source[path]=/"
  ```

Within a minute or two the manifest is online. Later releases only update the branch; Pages
republishes on its own.

The site is served on the custom domain `kasu-meta.kasukasu.com` (Settings › Pages › Custom
domain), which GitHub keeps in a `CNAME` file on `gh-pages`. The release workflow only rewrites
`manifest.json` and `.nojekyll`, so the file stays; don't delete it. The old
`waterbluenewworld.github.io/KasuMeta/` address redirects to the domain.

```sh
curl -fsS https://kasu-meta.kasukasu.com/manifest.json
```

## 5. Check from a Jellyfin server

On a Jellyfin 12.x server: Dashboard › Plugins › Repositories › add
`https://kasu-meta.kasukasu.com/manifest.json`. KasuMeta and the new version
then appear in the Catalog. Servers that already have it installed are offered the update.

## If something goes wrong

- **The tag doesn't match the version:** the workflow stops before publishing anything.
  Delete the tag, fix the version, and tag again:

  ```sh
  git push origin :refs/tags/v1.0.0 && git tag -d v1.0.0
  ```

- **CI fails on the tag:** nothing is published. Fix the problem on `pre/kasu_meta`, bring
  `main` forward again, delete the tag as above, and tag the new commit.
- **The release was created but the manifest step failed:** fix the cause, then re-run only
  the failed job (`gh run rerun <run id> --failed`). Before that, delete the release, because
  `gh release create` refuses an existing one:

  ```sh
  gh release delete v1.0.0 --yes
  ```

- **A published version is broken:** don't reuse its number. Release a new version with the
  fix. To withdraw the broken one, delete its entry from `manifest.json` on `gh-pages` by hand
  and push. Servers that installed it keep it until they update.
