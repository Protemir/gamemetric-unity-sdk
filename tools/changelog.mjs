// Reading one release's section out of CHANGELOG.md.
//
// Shared by the tool that prints release notes and the check that runs on every push, so the
// rule about what counts as a usable section is written once. Two copies would drift, and
// the copy that drifts is the one in the check — which is the copy whose whole job is to
// agree with the other.

/** Matches a release heading: "## [1.4.0] - 2026-08-08". */
const releaseHeading = /^## \[(\d+\.\d+\.\d+)\]/;

/**
 * The body of one release's section, or null when there is no such section.
 *
 * Scanned line by line rather than with one regex: the obvious pattern needs both "start of
 * line" and "end of input", and in JavaScript the /m/ flag that gives the first redefines $
 * to mean end of line, which truncates the section at its first blank line.
 */
export const sectionFor = (changelog, version) => {
  const lines = changelog.split('\n');
  const start = lines.findIndex((line) => line.startsWith(`## [${version}]`));

  if (start === -1) return null;

  const rest = lines.slice(start + 1);
  const nextHeading = rest.findIndex((line) => releaseHeading.test(line));

  return (nextHeading === -1 ? rest : rest.slice(0, nextHeading)).join('\n').trim();
};

/** The version of the newest release section, or null when the file has none. */
export const newestVersion = (changelog) =>
  changelog.split('\n').find((line) => releaseHeading.test(line))?.match(releaseHeading)[1] ?? null;
