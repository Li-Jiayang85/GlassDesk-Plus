using System.Drawing;
using System.Windows.Forms;

namespace GlassDeskPlusSetup;

internal sealed class InstallerForm : Form
{
    private readonly Label _status;
    private readonly ProgressBar _progress;
    private readonly Button _close;
    private readonly IProgress<string> _reporter;

    public int ExitCode { get; private set; }

    public InstallerForm()
    {
        Text = "GlassDesk Plus 安装程序";
        Width = 520;
        Height = 220;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.FromArgb(24, 24, 27);
        ForeColor = Color.White;

        var title = new Label
        {
            Text = "GlassDesk Plus",
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(28, 24),
            AutoSize = true,
        };
        var subtitle = new Label
        {
            Text = "GlassDesk 1.4.4 · 喜羊羊增强版",
            ForeColor = Color.FromArgb(180, 180, 190),
            Location = new Point(30, 62),
            AutoSize = true,
        };
        _status = new Label
        {
            Text = "准备安装…",
            ForeColor = Color.FromArgb(200, 200, 210),
            Location = new Point(30, 100),
            AutoSize = true,
        };
        _progress = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Location = new Point(30, 128),
            Size = new Size(455, 8),
        };
        _close = new Button
        {
            Text = "关闭",
            Enabled = false,
            Location = new Point(395, 152),
            Size = new Size(90, 30),
        };
        _close.Click += (_, _) => Close();

        Controls.AddRange([title, subtitle, _status, _progress, _close]);

        _reporter = new Progress<string>(message => _status.Text = message);
        Shown += (_, _) => StartInstall();
    }

    private async void StartInstall()
    {
        try
        {
            await Task.Run(() => Program.Install(_reporter, silent: false));
            _status.Text = "安装完成，GlassDesk 正在启动。";
            ExitCode = 0;
        }
        catch (Exception ex)
        {
            _status.Text = "安装失败：" + ex.Message;
            ExitCode = 1;
            Program.TryLog("interactive install failed: " + ex);
        }
        finally
        {
            _progress.Style = ProgressBarStyle.Blocks;
            _close.Enabled = true;
        }
    }
}
