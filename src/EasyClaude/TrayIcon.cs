using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using EasyClaude.Core;
using LinkState = EasyClaude.Core.LinkState;

namespace EasyClaude;
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Dictionary<LinkState, Icon> _icons = new();
    private readonly List<ToolStripItem> _actions = new();
    private readonly ContextMenuStrip _menu = new();
    private LinkState? _last;
    public TrayIcon(Action show, Func<Task> recheck, Func<Task> restartProxy, Func<Task> restartProgram,
        Func<Task> reimport, Action advanced, Action openLogs, Func<Task> exit)
    {
        foreach (var (state, name) in new[] { (LinkState.Ready, "Ready"), (LinkState.Degraded, "Warning"),
            (LinkState.Paused, "Warning"), (LinkState.Starting, "Starting"), (LinkState.Error, "Error"), (LinkState.Stopped, "Stopped") })
        {
            using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Tray" + name + ".ico")).Stream;
            _icons[state] = new Icon(stream, 32, 32);
        }
        _menu.Items.Add("显示窗口", null, (_, _) => show());
        Add("重新检测连接", recheck);
        Add("重启代理", restartProxy).ToolTipText = "只重新启动代理服务";
        Add("重启程序", restartProgram).ToolTipText = "重新启动 Easy Claude 和连接服务";
        _menu.Items.Add(new ToolStripSeparator());
        Add("导入节点配置…", reimport);
        _actions.Add(_menu.Items.Add("高级设置…", null, (_, _) => advanced()));
        _menu.Items.Add("打开日志目录", null, (_, _) => openLogs());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出程序", null, async (_, _) => await exit());
        _icon = new NotifyIcon { Icon = _icons[LinkState.Stopped], Text = "Easy Claude", Visible = true, ContextMenuStrip = _menu };
        _icon.DoubleClick += (_, _) => show();
    }
    private ToolStripItem Add(string label, Func<Task> action)
    {
        var item = _menu.Items.Add(label, null, async (_, _) =>
        {
            try { foreach (var i in _actions) i.Enabled = false; await action(); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Easy Claude"); }
            finally { foreach (var i in _actions) i.Enabled = _last != LinkState.Starting; }
        });
        _actions.Add(item);
        return item;
    }
    public void Update(LinkStatus status)
    {
        _icon.Icon = _icons[status.State];
        var text = "Easy Claude · " + status.Message;
        _icon.Text = text.Length > 63 ? text[..63] : text;
        foreach (var item in _actions) item.Enabled = status.State != LinkState.Starting;
        if (status.State == LinkState.Error && _last != LinkState.Error)
            _icon.ShowBalloonTip(5000, "Easy Claude · 连接异常", status.Message, ToolTipIcon.Warning);
        _last = status.State;
    }
    public void Dispose()
    {
        _icon.Visible = false; _icon.Dispose(); _menu.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
    }
}
