// Release consistency check. Unity cannot run in CI here, so this covers the mistakes that
// do not need Unity to catch: malformed manifests, a version that disagrees with the
// changelog, and content the manifest promises that is not actually in the repository.
//
// Everything is checked against `git ls-files`, not the working copy. That distinction is
// the whole point: Samples~ sat on disk for every release while .gitignore quietly kept it
// out of git, so a check that stat()s the filesystem passes on the maintainer's machine and
// still ships a package missing the content it advertises. What consumers receive is what
// is committed.
import { readFileSync, existsSync, readdirSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { newestVersion, sectionFor } from './changelog.mjs';

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
const asmdefPaths = [
  'Runtime/GameMetricSDK.Runtime.asmdef',
  'Editor/GameMetricSDK.Editor.asmdef',
  'Tests/GameMetricSDK.Tests.asmdef',
];

/** Parsed asmdefs, keyed by path. Entries that failed to parse are simply absent. */
const asmdefs = new Map();

for (const asmdef of asmdefPaths) {
  requireInRepository(asmdef, 'assembly definition');
  if (existsSync(asmdef)) {
    try {
      asmdefs.set(asmdef, JSON.parse(readFileSync(asmdef, 'utf8')));
    } catch (e) {
      problems.push(`${asmdef} is not valid JSON: ${e.message}`);
    }
  }
}

// How the three assemblies are wired together. Unity resolves references by assembly name,
// so a rename breaks every reference to it — and reports that only when the project is
// opened, which for a package means at the consumer, not here.
const assemblyNames = new Set([...asmdefs.values()].map((a) => a.name));

// Unity's own assemblies and precompiled DLLs are not ours to resolve.
const external = /^(Unity|UnityEngine|UnityEditor|nunit)/;

for (const [path, asmdef] of asmdefs) {
  const expected = path.slice(path.lastIndexOf('/') + 1, -'.asmdef'.length);
  if (asmdef.name !== expected) {
    problems.push(`${path} declares name "${asmdef.name}" but Unity expects "${expected}"`);
  }

  for (const reference of asmdef.references ?? []) {
    if (external.test(reference)) continue;
    if (!assemblyNames.has(reference)) {
      problems.push(`${path} references "${reference}", which no assembly in this package defines`);
    }
  }
}

const runtime = asmdefs.get('Runtime/GameMetricSDK.Runtime.asmdef');
const tests = asmdefs.get('Tests/GameMetricSDK.Tests.asmdef');

// The failure this catches is a silent one: an editor-only Runtime assembly compiles, the
// package imports cleanly, the game builds — and ships with no analytics at all, because
// none of the SDK is in the player.
if (runtime && (runtime.includePlatforms ?? []).length > 0) {
  problems.push(
    `Runtime assembly is limited to platforms [${runtime.includePlatforms.join(', ')}] — ` +
      'it must ship everywhere, or builds silently contain no SDK',
  );
}

// The mirror image: test code compiled into a player build. It would drag NUnit into the
// shipped game and is exactly what these two settings exist to prevent.
if (tests) {
  if (!(tests.includePlatforms ?? []).includes('Editor')) {
    problems.push('Tests assembly is not restricted to the Editor — test code would ship in builds');
  }
  if (!(tests.defineConstraints ?? []).includes('UNITY_INCLUDE_TESTS')) {
    problems.push('Tests assembly is missing the UNITY_INCLUDE_TESTS define constraint');
  }
  if (!(tests.references ?? []).includes(runtime?.name)) {
    problems.push('Tests assembly does not reference the Runtime assembly — nothing would be tested');
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

// The same check one level down. "Runtime/ has at least one tracked file" passes while a
// single new file sits untracked, and that file is missing from every consumer's package —
// Unity then fails to compile the assembly against a type that exists only on the
// maintainer's machine. This is the Samples~ failure again, per file instead of per folder,
// and per file is where it actually happens: one .gitignore line, one forgotten `git add`.
for (const folder of ['Runtime', 'Editor', 'Tests', 'Samples~']) {
  if (!existsSync(folder)) continue;

  for (const file of readdirSync(folder, { recursive: true, withFileTypes: true })) {
    if (!file.isFile()) continue;

    // parentPath is the directory the entry was found in, already relative to cwd.
    const path = `${file.parentPath ?? folder}/${file.name}`.replaceAll('\\', '/');

    // Unity's own build leftovers are meant to be absent.
    if (/\.(meta|csproj|user)$/.test(path)) continue;

    if (!tracked.has(path)) {
      problems.push(`${path} exists on disk but is NOT tracked by git, so it will not ship`);
    }
  }
}

// Tests/README.md is a map of what is covered and, more usefully, what is not. It only earns
// that trust while it is complete: three suites had been added without ever reaching the
// table, so the document quietly under-reported coverage — and a reader planning work from
// its "Not covered" section is exactly the reader who cannot afford that.
if (existsSync('Tests') && existsSync('Tests/README.md')) {
  const coverage = readFileSync('Tests/README.md', 'utf8');

  for (const file of readdirSync('Tests')) {
    if (!file.endsWith('Tests.cs')) continue;

    const suite = file.slice(0, -'.cs'.length);
    if (!coverage.includes(suite)) {
      problems.push(`Tests/README.md does not mention ${suite} — the coverage table is out of date`);
    }
  }
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

const changelog = readFileSync('CHANGELOG.md', 'utf8');
const changelogTop = newestVersion(changelog);

if (!changelogTop) {
  problems.push('CHANGELOG.md has no "## [x.y.z]" release heading');
} else if (changelogTop !== pkg.version) {
  problems.push(
    `version mismatch: package.json says ${pkg.version}, newest CHANGELOG entry is ${changelogTop}`,
  );
} else if (sectionFor(changelog, changelogTop) === '') {
  // The section is the release notes, so an empty one means an empty GitHub release. That
  // failure is already caught — but at release time, by which point the tag has been pushed
  // and fixing it means deleting and re-pushing a tag someone may have already fetched.
  // Checked here it is a red build on a branch, which costs nothing.
  problems.push(`the "## [${changelogTop}]" section in CHANGELOG.md is empty — release notes come from it`);
}

if (problems.length > 0) {
  console.error('Package checks failed:\n' + problems.map((p) => `  - ${p}`).join('\n'));
  process.exit(1);
}

console.log(`package ${pkg.name} ${pkg.version}: all checks passed (${tracked.size} tracked files)`);
