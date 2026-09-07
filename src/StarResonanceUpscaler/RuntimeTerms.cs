using System.IO;
using Forms = System.Windows.Forms;

internal static class RuntimeTerms
{
    internal static bool Accept()
    {
        var marker = Path.Combine(AppInfo.DataFolder, "runtime-terms-v1.accepted");
        if (File.Exists(marker)) return true;
        using var form = new Forms.Form { Text = "利用条件の確認", Width = 740, Height = 560, StartPosition = Forms.FormStartPosition.CenterScreen };
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-TERMS.md");
        if (!File.Exists(path))
        {
            Forms.MessageBox.Show("ライセンス案内が見つかりません。配布ZIPをすべて展開してください。", AppInfo.Name);
            return false;
        }
        var text = new Forms.TextBox { Multiline = true, ReadOnly = true, ScrollBars = Forms.ScrollBars.Vertical, Dock = Forms.DockStyle.Fill, Text = File.ReadAllText(path) };
        var view = new Forms.Button { Text = "原文ライセンスを開く", Dock = Forms.DockStyle.Bottom, Height = 36 };
        view.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "licenses")) { UseShellExecute = true });
        var accept = new Forms.Button { Text = "同意して続ける", Dock = Forms.DockStyle.Bottom, Height = 40, DialogResult = Forms.DialogResult.OK };
        form.Controls.Add(text);
        form.Controls.Add(view);
        form.Controls.Add(accept);
        if (form.ShowDialog() != Forms.DialogResult.OK) return false;
        Directory.CreateDirectory(AppInfo.DataFolder);
        File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
        return true;
    }
}
