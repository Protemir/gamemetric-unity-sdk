// Release consistency check. Unity cannot run in CI here, so this covers the mistakes that
// do not need Unity to catch: malformed manifests, and a version that disagrees with the
// changelog. A package published with a stale version is worse than an unpublished one —
// consumers pin what the manifest claims, not what the changelog says.
import { readFileSync, existsSync } from 'node:fs';

const problems = [];

const pkg = JSON.parse(readFileSync('package.json', 'utf8'));

for (const field of ['name', 'version', 'displayName', 'description', 'unity', 'license']) {
  if (!pkg[field]) problems.push(`package.json is missing "${field}"`);
}

if (!/^\d+\.\d+\.\d+$/.test(pkg.version ?? '')) {
  problems.push(`package.json version "${pkg.version}" is not MAJOR.MINOR.PATCH`);
}

// Every asmdef must parse: a broken one fails compilation only once Unity opens the project.
for (const asmdef of ['Runtime/GameMetricSDK.Runtime.asmdef', 'Editor/GameMetricSDK.Editor.asmdef', 'Tests/GameMetricSDK.Tests.asmdef']) {
  if (!existsSync(asmdef)) { problems.push(`missing ${asmdef}`); continue; }
  try { JSON.parse(readFileSync(asmdef, 'utf8')); }
  catch (e) { problems.push(`${asmdef} is not valid JSON: ${e.message}`); }
}

// A sample path that does not exist shows up in the Package Manager as an import that fails.
for (const sample of pkg.samples ?? []) {
  if (!existsSync(sample.path)) problems.push(`sample path does not exist: ${sample.path}`);
}

const changelogTop = readFileSync('CHANGELOG.md', 'utf8').match(/^## \[(\d+\.\d+\.\d+)\]/m)?.[1];
if (!changelogTop) problems.push('CHANGELOG.md has no "## [x.y.z]" release heading');
else if (changelogTop !== pkg.version) {
  problems.push(`version mismatch: package.json says ${pkg.version}, newest CHANGELOG entry is ${changelogTop}`);
}

if (problems.length > 0) {
  console.error('Package checks failed:\n' + problems.map(p => `  - ${p}`).join('\n'));
  process.exit(1);
}

console.log(`package ${pkg.name} ${pkg.version}: all checks passed`);
