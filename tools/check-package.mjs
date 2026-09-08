// Release consistency check. Unity cannot run in CI here, so this covers the mistakes that
// do not need Unity to catch: malformed manifests, a version that disagrees with the
// changelog, and content the manifest promises that is not actually in the repository.
//
// Everything is checked against `git ls-files`, not the working copy. That distinction is
// the whole point: Samples~ sat on disk for every release while .gitignore quietly kept it
// out of git, so a check that stat()s the filesystem passes on the maintainer's machine and
// still ships a package missing the content it advertises. What consumers receive is what
// is committed.
import { readFileSync, existsSync } from 'node:fs';
import { execFileSync } from 'node:child_process';

const problems = [];

/** Every path git tracks, as a Set for exact lookups. */
const tracked = new Set(
  execFileSync('git', ['ls-files'], { encoding: 'utf8' })
    .split('\n')
    .filter(Boolean),
);

/** True when the path is a tracked file, or a directory holding at least one tracked file. */
const isInRepository = (path) => {
  const normalised = path.replace(/\/+$/, '');
  if (tracked.has(normalised)) return true;
  const asDirectory = `${normalised}/`;
  for (const entry of tracked) {
    if (entry.startsWith(asDirectory)) return true;
  }
  return false;
};

/** Flags the case that is easy to miss: present locally, absent from the package. */
const requireInRepository = (path, what) => {
  if (isInRepository(path)) return;
  problems.push(
    existsSync(path)
      ? `${what} exists on disk but is NOT tracked by git, so it will not ship: ${path} (check .gitignore)`
      : `${what} does not exist: ${path}`,
  );
};

const pkg = JSON.parse(readFileSync('package.json', 'utf8'));

for (const field of ['name', 'version', 'displayName', 'description', 'unity', 'license']) {
  if (!pkg[field]) problems.push(`package.json is missing "${field}"`);
}

if (!/^\d+\.\d+\.\d+$/.test(pkg.version ?? '')) {
  problems.push(`package.json version "${pkg.version}" is not MAJOR.MINOR.PATCH`);
}

// A broken asmdef fails compilation only once Unity opens the project.
for (const asmdef of [
  'Runtime/GameMetricSDK.Runtime.asmdef',
  'Editor/GameMetricSDK.Editor.asmdef',
  'Tests/GameMetricSDK.Tests.asmdef',
]) {
  requireInRepository(asmdef, 'assembly definition');
  if (existsSync(asmdef)) {
    try {
      JSON.parse(readFileSync(asmdef, 'utf8'));
    } catch (e) {
      problems.push(`${asmdef} is not valid JSON: ${e.message}`);
    }
  }
}

// A sample path missing from the package shows in the Package Manager as an Import that
// silently does nothing.
for (const sample of pkg.samples ?? []) {
  requireInRepository(sample.path, `sample "${sample.displayName ?? sample.path}"`);
}

// Files the manifest links to, and the ones a consumer expects to find.
for (const required of ['README.md', 'CHANGELOG.md', 'LICENSE']) {
  requireInRepository(required, 'required file');
}

// Runtime code that is not committed is the same failure as a missing sample, just louder.
if (!isInRepository('Runtime')) {
  problems.push('Runtime/ contains no tracked files — the package would ship without code');
}

// Relative links in the docs of a public package: a reader who follows one and gets a 404
// concludes the package is unmaintained, and renaming a file is all it takes to create one.
for (const doc of ['README.md', 'CONTRIBUTING.md', 'Tests/README.md']) {
  if (!existsSync(doc)) continue;

  const directory = doc.includes('/') ? doc.slice(0, doc.lastIndexOf('/') + 1) : '';

  for (const [, target] of readFileSync(doc, 'utf8').matchAll(/\]\(([^)]+)\)/g)) {
    // External links and in-page anchors are not ours to verify.
    if (/^(https?:|mailto:|#)/.test(target)) continue;

    const resolved = (directory + target.split('#')[0].replace(/^\.\//, '')).replace(/\/+$/, '');
    if (resolved && !isInRepository(resolved)) {
      problems.push(`${doc} links to "${target}", which is not in the repository`);
    }
  }
}

const changelogTop = readFileSync('CHANGELOG.md', 'utf8').match(/^## \[(\d+\.\d+\.\d+)\]/m)?.[1];
if (!changelogTop) {
  problems.push('CHANGELOG.md has no "## [x.y.z]" release heading');
} else if (changelogTop !== pkg.version) {
  problems.push(
    `version mismatch: package.json says ${pkg.version}, newest CHANGELOG entry is ${changelogTop}`,
  );
}

if (problems.length > 0) {
  console.error('Package checks failed:\n' + problems.map((p) => `  - ${p}`).join('\n'));
  process.exit(1);
}

console.log(`package ${pkg.name} ${pkg.version}: all checks passed (${tracked.size} tracked files)`);
