using System.Runtime.InteropServices;
using QuanShuo.Core.Copying;
using QuanShuo.Core.Models;
using QuanShuo.Core.Storage;
using QuanShuo.Core.Transcription;
using QuanShuo.Native.Recording;

namespace QuanShuo.Native.Diagnostics;

/// <summary>
/// 录音中编辑的回归测试。用真实的系统编辑框走一遍：按键发给控件本身，识别结果走 Merge，
/// 每一步断言全文和光标。纯语音记录的文件往返也在这里测。
/// </summary>
public static class SmokeLive
{
    private const int WmChar = 0x0102;
    private const int VkBack = 0x08;
    private const int WmImeStartComposition = 0x010D;
    private const int WmImeEndComposition = 0x010E;

    public static int Run(string outputPath)
    {
        var results = new List<string>();
        int failures = 0;

        void Check(string name, bool passed, string detail = "")
        {
            results.Add((passed ? "ok   " : "FAIL ") + name + (passed || detail.Length == 0 ? string.Empty : "  => " + detail));
            if (!passed) failures++;
        }

        void Case(string name, Action<LiveTextBox, List<string>> body)
        {
            using var form = new Form { ShowInTaskbar = false, Width = 400, Height = 300, StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) };
            using var box = new LiveTextBox { Dock = DockStyle.Fill };
            form.Controls.Add(box);
            form.Show();
            box.Focus();
            var commits = new List<string>();
            box.Committed += (_, _) => commits.Add(box.Text);
            try { body(box, commits); }
            catch (Exception exception) { Check(name, false, exception.GetType().Name + ": " + exception.Message); }
            form.Close();
        }

        void Message(LiveTextBox box, int message, int code)
        {
            SendMessage(box.Handle, message, new IntPtr(code), IntPtr.Zero);
            Application.DoEvents();
        }

        void Type(LiveTextBox box, string text)
        {
            foreach (char c in text) Message(box, WmChar, c);
        }

        void Backspace(LiveTextBox box, int count = 1)
        {
            for (int i = 0; i < count; i++) Message(box, WmChar, VkBack);
        }

        Case("中间结果被修订", (box, _) =>
        {
            box.Merge("今天", false);
            box.Merge("今天天气", false);
            string text = box.Merge("今天的天气很好。", true);
            Check("中间结果被修订时覆盖，不重复", text == "今天的天气很好。", text);
            Check("光标跟到末尾", box.SelectionStart == box.TextLength, box.SelectionStart.ToString());
        });

        Case("退格", (box, _) =>
        {
            box.Merge("我想去北京", false);
            Backspace(box, 2);
            Check("退格立刻生效", box.Text == "我想去", box.Text);
            string text = box.Merge("我想去北京吧", false);
            Check("新说的话接在删改后的位置", text == "我想去吧", text);
            text = box.Merge("我想去北京吧。", true);
            Check("句末标点也接上", text == "我想去吧。", text);
            text = box.Merge("我想去北京吧。明天出发。", true);
            Check("下一句完整写入", text == "我想去吧。明天出发。", text);
        });

        Case("改前面", (box, commits) =>
        {
            box.Merge("第一句。", true);
            box.Merge("第一句。第二", false);
            box.SelectionStart = 0;
            box.SelectionLength = 1;
            Type(box, "首");
            Check("改动生效", box.Text == "首一句。第二", box.Text);
            string text = box.Merge("第一句。第二句话", false);
            Check("修订的还是第二句", text == "首一句。第二句话", text);
            Check("光标留在用户改的地方", box.SelectionStart == 1, box.SelectionStart.ToString());
            Check("用户改动通知保存", commits.Contains("首一句。第二"), string.Join("|", commits));
        });

        Case("光标挪到中间", (box, _) =>
        {
            box.Merge("前面。后面。", true);
            box.SelectionStart = 3;
            string text = box.Merge("前面。后面。补充", false);
            Check("下一句写到光标处", text == "前面。补充后面。", text);
            text = box.Merge("前面。后面。补充的话。", true);
            Check("修订也留在光标处", text == "前面。补充的话。后面。", text);
            Check("光标跟着走", box.SelectionStart == "前面。补充的话。".Length, box.SelectionStart.ToString());
        });

        Case("识别中间打字", (box, _) =>
        {
            box.Merge("今天天气", false);
            box.SelectionStart = 2;
            Type(box, "的");
            Check("插入生效", box.Text == "今天的天气", box.Text);
            string text = box.Merge("今天天气很好", false);
            Check("之后说的话不丢", text == "今天的很好天气", text);
        });

        Case("冻结后被修订", (box, _) =>
        {
            box.Merge("我想", false);
            Backspace(box);
            string text = box.Merge("我相信你", false);
            Check("服务端修订冻结部分时新话不丢", text.EndsWith("信你", StringComparison.Ordinal), text);
        });

        Case("末尾打字", (box, _) =>
        {
            box.Merge("你好", false);
            Type(box, "呀");
            string text = box.Merge("你好世界", false);
            Check("末尾打字不打断识别", text == "你好世界呀", text);
            Check("光标还在用户打的字后面", box.SelectionStart == box.TextLength, box.SelectionStart.ToString());
        });

        Case("全选删除", (box, _) =>
        {
            box.Merge("不要了这句。", true);
            box.SelectAll();
            Backspace(box);
            string text = box.Merge("不要了这句。重新说。", true);
            Check("全选删除后只留新说的", text == "重新说。", text);
        });

        Case("输入法组字", (box, commits) =>
        {
            box.Merge("开头", false);
            Message(box, WmImeStartComposition, 0);
            string text = box.Merge("开头继续", false);
            Check("组字期间文字不动", text == "开头", text);
            Message(box, WmImeEndComposition, 0);
            Check("组字结束后补上", box.Text == "开头继续", box.Text);
            Check("补上的文字也要保存", commits.Contains("开头继续"), string.Join("|", commits));
        });

        Case("保持选区", (box, _) =>
        {
            box.Merge("甲乙丙。", true);
            box.SelectionStart = 1;
            box.SelectionLength = 2;
            string text = box.Merge("甲乙丙。丁", false);
            Check("新字写在选区之后", text == "甲乙丙丁。", text);
            Check("选区不变", box.SelectionStart == 1 && box.SelectionLength == 2, box.SelectionStart + "," + box.SelectionLength);
        });

        Case("重复结果", (box, _) =>
        {
            box.Merge("同一句。", true);
            string text = box.Merge("同一句。", true);
            Check("重复收到同一结果不重复写入", text == "同一句。", text);
        });

        Case("长文字", (box, _) =>
        {
            string text = box.Merge(new string('字', 40000), true);
            Check("超过 3 万字没有被截断", text.Length == 40000, text.Length.ToString());
        });

        Case("句间打字", (box, _) =>
        {
            box.Merge("甲。", true);
            Type(box, "乙");
            string text = box.Merge("甲。丙。", true);
            Check("句间打字后顺序正确", text == "甲。乙丙。", text);
        });

        Case("控制退格", (box, _) =>
        {
            box.Merge("你好", true);
            Message(box, WmChar, 0x7f);
            Check("Ctrl+退格不打出方块", box.Text == "你好", box.Text);
        });

        string directory = Path.Combine(Path.GetTempPath(), "quanshuo-smoke-live-" + Environment.ProcessId);
        try
        {
            var store = new RecordStore(directory);
            var metadata = new CaptureMetadata { Timestamp = DateTimeOffset.Now, AppName = "Weixin", WindowTitle = "聊天" };
            QuanShuoRecord record = store.CreateTextAsync(metadata, TranscriptionStatus.Recording).GetAwaiter().GetResult();
            Check("纯语音记录能被读出", store.ReadAllAsync().GetAwaiter().GetResult().Count == 1);
            Check("新记录没有图片", !record.HasImage);
            const string spoken = "![这句话看着像图片](a.png)\n第二行";
            store.SaveTextAsync(record, spoken, TranscriptionStatus.Completed).GetAwaiter().GetResult();
            QuanShuoRecord read = store.ReadAllAsync().GetAwaiter().GetResult().Single();
            Check("文字原样读回", read.Text == spoken, read.Text);
            Check("读回后仍是纯语音", !read.HasImage, read.ImagePath);
            Check("状态正确", read.Status == TranscriptionStatus.Completed, read.Status.ToString());
            string copied = AiCopyFormatter.Format(read);
            Check("复制给 AI 不带截图行", !copied.Contains("[截图]", StringComparison.Ordinal) && copied.Contains("[说明] ", StringComparison.Ordinal), copied);
            QuanShuoRecord blank = read with { Text = "  " };
            Check("批量复制文字跳过空记录", AiCopyFormatter.JoinTexts([read with { Text = " 第一条\n" }, blank, read with { Text = "第二条" }]) == "第一条\n\n第二条");
            Check("全是空记录时没有文字", AiCopyFormatter.JoinTexts([blank]).Length == 0);
            File.WriteAllText(store.GetAudioPath(read), "x");
            store.Discard(read);
            Check("丢弃后文件都没了", Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length == 0);
            store.SaveTextAsync(read, "不该写进去", TranscriptionStatus.Completed).GetAwaiter().GetResult();
            Check("已删除的记录不会被保存复活", Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length == 0);
            string oldFormat = "---\ntime: 2026-11-01T10:00:00.0000000+08:00\napp: \"A\"\nwindow: \"B\"\nimage: \"100000.png\"\nstatus: completed\n---\n![截图](100000.png)\n\n旧记录\n";
            QuanShuoRecord old = MarkdownRecordFile.Parse(Path.Combine(directory, "100000.md"), oldFormat);
            Check("旧记录照常解析", old.HasImage && old.Text == "旧记录", old.Text);

            var indexed = store.CreateTextAsync(metadata, TranscriptionStatus.Completed).GetAwaiter().GetResult();
            store.SaveTextAsync(indexed, "能被搜到的一句话", TranscriptionStatus.Completed).GetAwaiter().GetResult();
            var index = new History.RecordIndex(store);
            index.SynchronizeAsync().GetAwaiter().GetResult();
            IReadOnlyList<QuanShuoRecord> found = index.QueryAsync("搜到").GetAwaiter().GetResult();
            Check("纯语音记录能被索引和搜索", found.Count == 1 && !found[0].HasImage, found.Count.ToString());

            // 不同日期、同一时分秒的两条记录文件名相同，撤销用的备份不能互相覆盖
            var bin = new History.RecycleBinService(directory);
            DateTimeOffset moment = DateTimeOffset.Now;
            QuanShuoRecord today = store.CreateTextAsync(new CaptureMetadata { Timestamp = moment, AppName = "a", WindowTitle = "t" }, TranscriptionStatus.Completed).GetAwaiter().GetResult();
            QuanShuoRecord yesterday = store.CreateTextAsync(new CaptureMetadata { Timestamp = moment.AddDays(-1), AppName = "b", WindowTitle = "t" }, TranscriptionStatus.Completed).GetAwaiter().GetResult();
            store.SaveTextAsync(today, "今天这条", TranscriptionStatus.Completed).GetAwaiter().GetResult();
            store.SaveTextAsync(yesterday, "昨天这条", TranscriptionStatus.Completed).GetAwaiter().GetResult();
            bin.DeleteAsync([today, yesterday]).GetAwaiter().GetResult();
            Check("批量删除后文件都没了", !File.Exists(today.MarkdownPath) && !File.Exists(yesterday.MarkdownPath));
            bool undone = bin.UndoAsync().GetAwaiter().GetResult();
            Check("一次撤销全部回来", undone && File.Exists(today.MarkdownPath) && File.Exists(yesterday.MarkdownPath));
            Check("同名文件没有串", File.Exists(today.MarkdownPath) && File.ReadAllText(today.MarkdownPath).Contains("今天这条") && File.ReadAllText(yesterday.MarkdownPath).Contains("昨天这条"));

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        results.Add(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        File.WriteAllLines(outputPath, results);
        return failures == 0 ? 0 : 1;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
