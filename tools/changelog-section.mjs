// Prints the CHANGELOG section for one version, so release notes are the changelog rather
// than a second description of the same work written from memory. Two places saying what
// changed will disagree eventually, and the one nobody reads while writing is the one that
// goes stale.
//
// Usage: node tools/changelog-section.mjs 1.4.0
import { readFileSync } from 'node:fs';

const version = process.argv[2];

if (!version) {
  console.error('Usage: node tools/changelog-section.mjs <version>');
  process.exit(2);
}

const changelog = readFileSync('CHANGELOG.md', 'utf8');

// Scanned line by line rather than with one regex: the obvious pattern needs both "start of
// line" and "end of input", and in JavaScript the /m/ flag that gives the first redefines $
// to mean end of line, which truncates the section at its first blank line.
const lines = changelog.split('\n');
const isReleaseHeading = (line) => /^## \[\d+\.\d+\.\d+\]/.test(line);
const start = lines.findIndex((line) => line.startsWith(`## [${version}]`));

if (start === -1) {
  console.error(`CHANGELOG.md has no "## [${version}]" section.`);
  process.exit(1);
}

const rest = lines.slice(start + 1);
const nextHeading = rest.findIndex(isReleaseHeading);
const body = (nextHeading === -1 ? rest : rest.slice(0, nextHeading)).join('\n').trim();

if (body.length === 0) {
  console.error(`The "## [${version}]" section in CHANGELOG.md is empty.`);
  process.exit(1);
}

process.stdout.write(body + '\n');
