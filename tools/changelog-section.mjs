// Prints the CHANGELOG section for one version, so release notes are the changelog rather
// than a second description of the same work written from memory. Two places saying what
// changed will disagree eventually, and the one nobody reads while writing is the one that
// goes stale.
//
// Usage: node tools/changelog-section.mjs 1.4.0
import { readFileSync } from 'node:fs';
import { sectionFor } from './changelog.mjs';

const version = process.argv[2];

if (!version) {
  console.error('Usage: node tools/changelog-section.mjs <version>');
  process.exit(2);
}

const body = sectionFor(readFileSync('CHANGELOG.md', 'utf8'), version);

if (body === null) {
  console.error(`CHANGELOG.md has no "## [${version}]" section.`);
  process.exit(1);
}

if (body.length === 0) {
  console.error(`The "## [${version}]" section in CHANGELOG.md is empty.`);
  process.exit(1);
}

process.stdout.write(body + '\n');
