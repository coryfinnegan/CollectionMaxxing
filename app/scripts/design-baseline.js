// Pure logic for the design ratchet (#307): compare per-file per-rule eslint
// counts against the grandfathered baseline. Tested by design-baseline.test.js.
'use strict';

const RULES = ['cm-design/no-raw-colors', 'cm-design/no-inline-styles', 'cm-design/no-raw-text', 'cm-design/no-doubled-title', 'cm-design/no-gradients', 'cm-design/no-unistyles-third-party-styles'];

/** Collapse eslint JSON results into { file: { rule: { count, messages } } }. Keys use posix separators. */
function summarize(results, root) {
  const summary = {};
  for (const file of results || []) {
    const rel = file.filePath.replace(root, '').replace(/^[\\/]/, '').replace(/\\/g, '/');
    for (const message of file.messages || []) {
      if (!RULES.includes(message.ruleId)) continue;
      summary[rel] ??= {};
      summary[rel][message.ruleId] ??= { count: 0, messages: [] };
      summary[rel][message.ruleId].count += 1;
      summary[rel][message.ruleId].messages.push(message);
    }
  }
  return summary;
}

/**
 * Compare a summary against the baseline entries. Returns
 * { failures: [{ file, rule, count, allowed, messages }], paidOff: [{ file, rule }] }.
 * A file+rule fails only when its count EXCEEDS the baseline: counts may go down freely.
 */
function compareToBaseline(summary, baseline) {
  const allowed = new Map((baseline.entries || []).map((e) => [`${e.file} ${e.rule}`, e.count]));
  const failures = [];
  for (const [file, rules] of Object.entries(summary)) {
    for (const [rule, { count, messages }] of Object.entries(rules)) {
      const limit = allowed.get(`${file} ${rule}`) ?? 0;
      if (count > limit) failures.push({ file, rule, count, allowed: limit, messages });
    }
  }
  const paidOff = (baseline.entries || []).filter((e) => (summary[e.file] || {})[e.rule] === undefined).map((e) => ({ file: e.file, rule: e.rule }));
  return { failures, paidOff };
}

/** Rebuild baseline entries from a summary, keeping existing reasons where the file+rule survives. */
function buildEntries(summary, oldEntries, defaultReason) {
  const reasons = new Map((oldEntries || []).map((e) => [`${e.file} ${e.rule}`, e.reason]));
  return Object.entries(summary)
    .flatMap(([file, rules]) => Object.entries(rules).map(([rule, { count }]) => ({ file, rule, count })))
    .sort((a, b) => (a.file === b.file ? a.rule.localeCompare(b.rule) : a.file.localeCompare(b.file)))
    .map((e) => ({ ...e, reason: reasons.get(`${e.file} ${e.rule}`) || defaultReason(e.rule) }));
}

module.exports = { RULES, summarize, compareToBaseline, buildEntries };
