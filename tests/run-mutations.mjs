// Deliberately break one guard at a time. Always restore exact bytes, even on failure.
// Run with no concurrent builds/tests: node tests/run-mutations.mjs
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';

const dotnet = resolve(process.env.USERPROFILE, '.dotnet/dotnet.exe');
const core = ['run', '--project', 'tests/QuanShuo.Core.Tests', '--no-restore', '--'];
const mutations = [
  ['atomic-text', 'src/QuanShuo.Core/Storage/AtomicFile.cs', 'path + "." + Guid.NewGuid().ToString("N") + ".tmp"', 'path + ".tmp"', dotnet, [...core, 'atomic-text-concurrent'], /FAIL atomic-text-concurrent: IOException/],
  ['atomic-bytes', 'src/QuanShuo.Core/Storage/AtomicFile.cs', 'path + "." + Guid.NewGuid().ToString("N") + ".tmp"', 'path + ".tmp"', dotnet, [...core, 'atomic-bytes-concurrent'], /FAIL atomic-bytes-concurrent: IOException/, true],
  ['atomic-cleanup', 'src/QuanShuo.Core/Storage/AtomicFile.cs', 'TryDelete(temporaryPath);', '// cleanup removed', dotnet, [...core, 'atomic-cancel-preserves-original'], /FAIL atomic-cancel-preserves-original: InvalidOperationException/],
  ['atomic-reader', 'src/QuanShuo.Core/Storage/AtomicFile.cs', 'await PublishAsync(temporaryPath, path);', 'File.Move(temporaryPath, path, true);', dotnet, [...core, 'atomic-waits-for-brief-reader'], /FAIL atomic-waits-for-brief-reader: UnauthorizedAccessException/],
  ['missing-file', 'src/QuanShuo.Core/Storage/MarkdownRecordFile.cs', 'if (!File.Exists(markdownPath)) return false;', 'if (!File.Exists(markdownPath)) return true;', dotnet, [...core, 'missing-record-save-must-fail'], /FAIL missing-record-save-must-fail: InvalidOperationException/],
  ['markdown-body', 'src/QuanShuo.Core/Storage/MarkdownRecordFile.cs', "int firstLineEnd = text.IndexOf('\\n');", 'int firstLineEnd = text.IndexOf("\\n\\n", StringComparison.Ordinal);', dotnet, [...core, 'markdown-image-adjacent-paragraph'], /FAIL markdown-image-adjacent-paragraph: InvalidOperationException/],
  ['batch-settle', 'frontend/src/app/App.ts', 'if (!(await this.stage.settle())) {\n        this.refreshMarks();\n        return;\n      }', '', process.execPath, ['frontend/scripts/test-races.mjs', 'batch-delete-waits-for-draft'], /FAIL batch-delete-waits-for-draft: AssertionError/],
  ['retry-reopen', 'frontend/src/components/StageView.ts', 'editor.readOnly = this.retrying.has(record.id);', 'editor.readOnly = false;', process.execPath, ['frontend/scripts/test-races.mjs', 'retry-cannot-be-bypassed-by-selection'], /FAIL retry-cannot-be-bypassed-by-selection: AssertionError/],
  ['retry-selection', 'frontend/src/components/StageView.ts', 'if (this.records.length === 1 && this.records[0].id === record.id) this.show([updated]);', 'this.show([updated]);', process.execPath, ['frontend/scripts/test-races.mjs', 'retry-save-must-not-replace-new-selection'], /FAIL retry-save-must-not-replace-new-selection: AssertionError/],
  ['query-order', 'frontend/src/app/App.ts', 'if (loadVersion !== this.loadVersion) return;', '', process.execPath, ['frontend/scripts/test-races.mjs', 'latest-search-wins'], /FAIL latest-search-wins: AssertionError/],
  ['delete-selection', 'frontend/src/components/StageView.ts', 'if (this.records.length === 1 && this.records[0].id === record.id) {\n      this.records = [];', 'if (true) {\n      this.records = [];', process.execPath, ['frontend/scripts/test-races.mjs', 'delete-result-keeps-new-selection'], /FAIL delete-result-keeps-new-selection: AssertionError/],
  ['query-edit', 'frontend/src/app/App.ts', 'if (editVersion !== this.editVersion) return this.loadRecords(animate);', '', process.execPath, ['frontend/scripts/test-races.mjs', 'old-query-cannot-revert-saved-text'], /FAIL old-query-cannot-revert-saved-text: AssertionError/],
  ['view-open', 'frontend/src/app/App.ts', "if (state === 'settings') void this.settings.show();", "if (state === 'never') void this.settings.show();", process.execPath, ['frontend/scripts/test-races.mjs', 'view-open-settings-keeps-draft'], /FAIL view-open-settings-keeps-draft: AssertionError/],
  ['existing-copy-test', 'frontend/src/app/App.ts', 'if (!(await this.stage.settle())) {', 'if (false) {', process.execPath, ['frontend/scripts/test-drafts.mjs'], /失败.*输入后立刻复制/, true],
];
mkdirSync('artifacts/mutations', { recursive: true });
let failures = 0;
for (const [name, file, from, to, command, args, expected, last] of mutations) {
  const bytes = readFileSync(file);
  const source = bytes.toString('utf8');
  const newline = source.includes('\r\n') ? '\r\n' : '\n';
  const needle = from.replaceAll('\n', newline);
  const index = last ? source.lastIndexOf(needle) : source.indexOf(needle);
  if (index < 0) throw new Error(`Missing mutation target: ${name}`);
  try {
    writeFileSync(file, source.slice(0, index) + to.replaceAll('\n', newline) + source.slice(index + needle.length));
    const result = spawnSync(command, args, { encoding: 'utf8', windowsHide: true, timeout: 60_000 });
    const output = `${result.stdout ?? ''}\n${result.stderr ?? ''}`;
    writeFileSync(`artifacts/mutations/${name}.log`, output);
    const caught = result.status === 1 && expected.test(output) && !/TimeoutError|error CS\d+|ERR_MODULE_NOT_FOUND/.test(output);
    console.log(`${caught ? 'CAUGHT' : 'NOT VERIFIED'} ${name}`);
    if (!caught) { failures++; console.log(output); }
  } finally { writeFileSync(file, bytes); }
}
process.exitCode = failures ? 1 : 0;
