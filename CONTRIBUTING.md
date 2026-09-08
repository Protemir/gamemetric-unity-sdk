# Contributing

## Running the tests

Open the project in Unity → **Window → General → Test Runner → EditMode → Run All**.
What is covered, and what deliberately is not, is listed in
[`Tests/README.md`](./Tests/README.md).

The package checks run without Unity:

```bash
node tools/check-package.mjs
```

They validate the manifest and every assembly definition, confirm the newest
`CHANGELOG.md` entry matches `package.json`, and — the part worth knowing about —
resolve every advertised path against `git ls-files` rather than the filesystem.
A file that exists only in your working copy does not ship, and that is how the
demo sample went missing from several releases while looking fine locally.

## Releasing

Every user-visible change gets a changelog entry and a release. The changelog is
the single description of what changed; release notes are generated from it, so
there is no second place to keep in sync.

1. **Write the changelog entry** in `CHANGELOG.md` under a new
   `## [x.y.z] - YYYY-MM-DD` heading, following
   [Keep a Changelog](https://keepachangelog.com/en/1.1.0/): `Added`, `Changed`,
   `Fixed`, `Removed`. Write it for someone deciding whether to upgrade — what
   changed for them, and anything they must do.
2. **Bump `version` in `package.json`** to the same number, following
   [Semantic Versioning](https://semver.org/). Patch for fixes, minor for new
   API, major for a break. Bump only when code under `Runtime/` or `Editor/`
   changed: a version whose diff is documentation teaches people to ignore
   updates.
3. **Commit, then tag and push:**

   ```bash
   git tag v1.4.1
   git push origin main --follow-tags
   ```

4. The `Release` workflow takes it from there. It refuses to publish if the tag
   disagrees with `package.json`, re-runs the package checks (a tag can point at
   a commit CI never saw), extracts the section for that version from
   `CHANGELOG.md`, and publishes it as the GitHub release.

If the release does not appear, check the workflow run before touching anything
by hand — publishing from the UI instead leaves the automated path untested and
the next release fails the same way.
