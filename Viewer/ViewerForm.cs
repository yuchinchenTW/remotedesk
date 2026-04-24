using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SimpleRemote.Shared;

namespace SimpleRemote.Viewer
{
    internal sealed class ViewerForm : Form
    {
        private const double MinZoom = 0.25;
        private const double MaxZoom = 4.0;
        private const double ZoomStep = 1.25;

        private readonly TextBox _hostTextBox;
        private readonly TextBox _portTextBox;
        private readonly TextBox _passwordTextBox;
        private readonly Button _connectButton;
        private readonly Button _disconnectButton;
        private readonly Button _zoomOutButton;
        private readonly Button _zoomResetButton;
        private readonly Button _zoomInButton;
        private readonly Label _zoomLabel;
        private readonly Panel _imagePanel;
        private readonly PictureBox _pictureBox;
        private readonly ListView _hostsListView;
        private readonly Label _statusLabel;
        private readonly Label _infoLabel;
        private readonly Timer _discoveryRefreshTimer;

        private RemoteViewerClient _client;
        private HostDiscoveryListener _discoveryListener;
        private Bitmap _currentFrame;
        private Size _remoteSize;
        private double _zoomFactor = 1.0;
        private readonly Dictionary<string, DiscoveredHostInfo> _discoveredHosts = new Dictionary<string, DiscoveredHostInfo>();

        public ViewerForm()
        {
            Text = "Simple Remote Viewer";
            Width = 1320;
            Height = 760;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            var topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 92
            };

            var hostLabel = new Label
            {
                Left = 12,
                Top = 14,
                Width = 48,
                Text = "Host"
            };

            _hostTextBox = new TextBox
            {
                Left = 58,
                Top = 10,
                Width = 170,
                Text = "127.0.0.1"
            };

            var portLabel = new Label
            {
                Left = 244,
                Top = 14,
                Width = 38,
                Text = "Port"
            };

            _portTextBox = new TextBox
            {
                Left = 282,
                Top = 10,
                Width = 72,
                Text = "5901"
            };

            var passwordLabel = new Label
            {
                Left = 370,
                Top = 14,
                Width = 66,
                Text = "Password"
            };

            _passwordTextBox = new TextBox
            {
                Left = 438,
                Top = 10,
                Width = 170,
                UseSystemPasswordChar = true,
                Text = "changeme"
            };

            _connectButton = new Button
            {
                Left = 626,
                Top = 8,
                Width = 100,
                Text = "Connect"
            };
            _connectButton.Click += ConnectButton_Click;

            _disconnectButton = new Button
            {
                Left = 734,
                Top = 8,
                Width = 100,
                Text = "Disconnect",
                Enabled = false
            };
            _disconnectButton.Click += DisconnectButton_Click;

            _zoomOutButton = new Button
            {
                Left = 850,
                Top = 8,
                Width = 34,
                Text = "-"
            };
            _zoomOutButton.Click += ZoomOutButton_Click;

            _zoomResetButton = new Button
            {
                Left = 888,
                Top = 8,
                Width = 58,
                Text = "100%"
            };
            _zoomResetButton.Click += ZoomResetButton_Click;

            _zoomInButton = new Button
            {
                Left = 950,
                Top = 8,
                Width = 34,
                Text = "+"
            };
            _zoomInButton.Click += ZoomInButton_Click;

            _zoomLabel = new Label
            {
                Left = 992,
                Top = 14,
                Width = 100,
                Text = "Zoom: Fit"
            };

            _statusLabel = new Label
            {
                Left = 12,
                Top = 46,
                Width = 1080,
                Height = 18,
                Text = "Status: Not connected."
            };

            _infoLabel = new Label
            {
                Left = 12,
                Top = 64,
                Width = 1280,
                Height = 18,
                Text = "Hosts on the same LAN auto-appear on the right. Ctrl+V sends local clipboard text to the remote PC. Ctrl+MouseWheel zooms."
            };

            topPanel.Controls.Add(hostLabel);
            topPanel.Controls.Add(_hostTextBox);
            topPanel.Controls.Add(portLabel);
            topPanel.Controls.Add(_portTextBox);
            topPanel.Controls.Add(passwordLabel);
            topPanel.Controls.Add(_passwordTextBox);
            topPanel.Controls.Add(_connectButton);
            topPanel.Controls.Add(_disconnectButton);
            topPanel.Controls.Add(_zoomOutButton);
            topPanel.Controls.Add(_zoomResetButton);
            topPanel.Controls.Add(_zoomInButton);
            topPanel.Controls.Add(_zoomLabel);
            topPanel.Controls.Add(_statusLabel);
            topPanel.Controls.Add(_infoLabel);

            _hostsListView = new ListView
            {
                Dock = DockStyle.Right,
                Width = 360,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false
            };
            _hostsListView.Columns.Add("Name", 130);
            _hostsListView.Columns.Add("IP", 110);
            _hostsListView.Columns.Add("Port", 50);
            _hostsListView.Columns.Add("Seen", 55);
            _hostsListView.SelectedIndexChanged += HostsListView_SelectedIndexChanged;
            _hostsListView.DoubleClick += HostsListView_DoubleClick;

            _imagePanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                AutoScroll = true
            };
            _imagePanel.Resize += ImagePanel_Resize;

            _pictureBox = new PictureBox
            {
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.StretchImage,
                TabStop = true
            };
            _pictureBox.MouseDown += PictureBox_MouseDown;
            _pictureBox.MouseUp += PictureBox_MouseUp;
            _pictureBox.MouseMove += PictureBox_MouseMove;
            _pictureBox.MouseWheel += PictureBox_MouseWheel;
            _pictureBox.MouseClick += PictureBox_MouseClick;
            _pictureBox.MouseEnter += PictureBox_MouseEnter;

            _imagePanel.Controls.Add(_pictureBox);

            Controls.Add(topPanel);
            Controls.Add(_imagePanel);
            Controls.Add(_hostsListView);

            KeyDown += ViewerForm_KeyDown;
            KeyUp += ViewerForm_KeyUp;
            FormClosed += ViewerForm_FormClosed;
            MouseWheel += ViewerForm_MouseWheel;

            _discoveryListener = new HostDiscoveryListener(OnHostDiscovered);
            _discoveryListener.Start();

            _discoveryRefreshTimer = new Timer();
            _discoveryRefreshTimer.Interval = 1000;
            _discoveryRefreshTimer.Tick += DiscoveryRefreshTimer_Tick;
            _discoveryRefreshTimer.Start();
        }

        private void ZoomOutButton_Click(object sender, EventArgs e)
        {
            ApplyZoom(_zoomFactor / ZoomStep);
        }

        private void ZoomResetButton_Click(object sender, EventArgs e)
        {
            ApplyZoom(1.0);
        }

        private void ZoomInButton_Click(object sender, EventArgs e)
        {
            ApplyZoom(_zoomFactor * ZoomStep);
        }

        private void ViewerForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            if (_discoveryRefreshTimer != null)
            {
                _discoveryRefreshTimer.Stop();
                _discoveryRefreshTimer.Dispose();
            }

            if (_discoveryListener != null)
            {
                _discoveryListener.Dispose();
                _discoveryListener = null;
            }

            if (_client != null)
            {
                _client.Dispose();
            }

            if (_currentFrame != null)
            {
                _currentFrame.Dispose();
                _currentFrame = null;
            }
        }

        private void DiscoveryRefreshTimer_Tick(object sender, EventArgs e)
        {
            RefreshDiscoveredHosts();
        }

        private void ConnectButton_Click(object sender, EventArgs e)
        {
            int port;
            if (!int.TryParse(_portTextBox.Text, out port) || port < 1 || port > 65535)
            {
                MessageBox.Show(this, "Enter a valid TCP port.", "Invalid Port", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                _client = new RemoteViewerClient(UpdateStatus, UpdateFrame);
                _client.Connect(_hostTextBox.Text.Trim(), port, _passwordTextBox.Text);
                _connectButton.Enabled = false;
                _disconnectButton.Enabled = true;
                _hostTextBox.Enabled = false;
                _portTextBox.Enabled = false;
                _passwordTextBox.Enabled = false;
                _pictureBox.Focus();
            }
            catch (Exception ex)
            {
                if (_client != null)
                {
                    _client.Dispose();
                    _client = null;
                }

                MessageBox.Show(this, ex.Message, "Connection Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatus("Connection failed.");
            }
        }

        private void DisconnectButton_Click(object sender, EventArgs e)
        {
            ResetConnection();
        }

        private void HostsListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_hostsListView.SelectedItems.Count == 0)
            {
                return;
            }

            var item = _hostsListView.SelectedItems[0];
            var info = item.Tag as DiscoveredHostInfo;
            if (info == null)
            {
                return;
            }

            _hostTextBox.Text = info.HostAddress;
            _portTextBox.Text = info.HostPort.ToString();
        }

        private void HostsListView_DoubleClick(object sender, EventArgs e)
        {
            if (_hostsListView.SelectedItems.Count == 0 || !_connectButton.Enabled)
            {
                return;
            }

            ConnectButton_Click(sender, e);
        }

        private void PictureBox_MouseClick(object sender, MouseEventArgs e)
        {
            _pictureBox.Focus();
        }

        private void PictureBox_MouseEnter(object sender, EventArgs e)
        {
            _pictureBox.Focus();
        }

        private void PictureBox_MouseMove(object sender, MouseEventArgs e)
        {
            Point remotePoint;
            if (!TryTranslatePoint(e.Location, out remotePoint))
            {
                return;
            }

            if (_client != null)
            {
                _client.SendMouseMove(remotePoint.X, remotePoint.Y);
            }
        }

        private void PictureBox_MouseDown(object sender, MouseEventArgs e)
        {
            _pictureBox.Focus();

            Point remotePoint;
            if (!TryTranslatePoint(e.Location, out remotePoint))
            {
                return;
            }

            if (_client != null)
            {
                _client.SendMouseButton(remotePoint.X, remotePoint.Y, TranslateButton(e.Button), true);
            }
        }

        private void PictureBox_MouseUp(object sender, MouseEventArgs e)
        {
            Point remotePoint;
            if (!TryTranslatePoint(e.Location, out remotePoint))
            {
                return;
            }

            if (_client != null)
            {
                _client.SendMouseButton(remotePoint.X, remotePoint.Y, TranslateButton(e.Button), false);
            }
        }

        private void PictureBox_MouseWheel(object sender, MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) == Keys.Control)
            {
                ApplyZoom(e.Delta > 0 ? _zoomFactor * ZoomStep : _zoomFactor / ZoomStep);
                return;
            }

            Point remotePoint;
            if (!TryTranslatePoint(e.Location, out remotePoint))
            {
                return;
            }

            if (_client != null)
            {
                _client.SendMouseWheel(remotePoint.X, remotePoint.Y, e.Delta);
            }
        }

        private void ViewerForm_MouseWheel(object sender, MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) != Keys.Control)
            {
                return;
            }

            ApplyZoom(e.Delta > 0 ? _zoomFactor * ZoomStep : _zoomFactor / ZoomStep);
        }

        private void ViewerForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (_client != null && _pictureBox.Focused)
            {
                if (e.Control && e.KeyCode == Keys.V)
                {
                    var text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
                    if (!string.IsNullOrEmpty(text))
                    {
                        _client.SendClipboardText(text);
                        UpdateStatus("Sent local clipboard text to remote host.");
                    }
                    else
                    {
                        UpdateStatus("Local clipboard does not contain text.");
                    }

                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return;
                }

                _client.SendKey(e.KeyValue, true);
                e.Handled = true;
            }
        }

        private void ViewerForm_KeyUp(object sender, KeyEventArgs e)
        {
            if (_client != null && _pictureBox.Focused)
            {
                _client.SendKey(e.KeyValue, false);
                e.Handled = true;
            }
        }

        private void OnHostDiscovered(DiscoveredHostInfo host)
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(new Action<DiscoveredHostInfo>(OnHostDiscovered), host);
                return;
            }

            _discoveredHosts[BuildHostKey(host)] = host;
            RefreshDiscoveredHosts();
        }

        private void UpdateStatus(string text)
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(UpdateStatus), text);
                return;
            }

            _statusLabel.Text = "Status: " + text;

            if (text == "Disconnected.")
            {
                ResetConnectionUiOnly();
            }
        }

        private void UpdateFrame(FrameUpdate update)
        {
            if (IsDisposed)
            {
                update.Image.Dispose();
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(new Action<FrameUpdate>(UpdateFrame), update);
                return;
            }

            _remoteSize = new Size(update.DesktopWidth, update.DesktopHeight);

            if (update.IsFullFrame || _currentFrame == null || _currentFrame.Width != update.DesktopWidth || _currentFrame.Height != update.DesktopHeight)
            {
                var previous = _currentFrame;
                _currentFrame = update.Image;
                _pictureBox.Image = _currentFrame;
                UpdateImageLayout();

                if (previous != null)
                {
                    previous.Dispose();
                }

                return;
            }

            using (var graphics = Graphics.FromImage(_currentFrame))
            {
                graphics.DrawImageUnscaled(update.Image, update.X, update.Y);
            }

            update.Image.Dispose();
            _pictureBox.Invalidate();
            UpdateImageLayout();
        }

        private void ResetConnection()
        {
            if (_client != null)
            {
                _client.Dispose();
                _client = null;
            }

            ResetConnectionUiOnly();
            UpdateStatus("Disconnected.");
        }

        private void ResetConnectionUiOnly()
        {
            _connectButton.Enabled = true;
            _disconnectButton.Enabled = false;
            _hostTextBox.Enabled = true;
            _portTextBox.Enabled = true;
            _passwordTextBox.Enabled = true;
        }

        private void RefreshDiscoveredHosts()
        {
            var now = DateTime.UtcNow;
            var expiredKeys = _discoveredHosts
                .Where(pair => (now - pair.Value.LastSeenUtc).TotalMilliseconds > DiscoveryProtocol.HostTimeoutMs)
                .Select(pair => pair.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _discoveredHosts.Remove(key);
            }

            var selectedKey = _hostsListView.SelectedItems.Count > 0
                ? _hostsListView.SelectedItems[0].Name
                : null;

            _hostsListView.BeginUpdate();
            _hostsListView.Items.Clear();

            foreach (var host in _discoveredHosts.Values
                .OrderBy(host => GetHostDisplayName(host), StringComparer.OrdinalIgnoreCase)
                .ThenBy(host => host.HostAddress, StringComparer.Ordinal))
            {
                var seenSeconds = Math.Max(0, (int)Math.Round((now - host.LastSeenUtc).TotalSeconds));
                var item = new ListViewItem(GetHostDisplayName(host));
                item.Name = BuildHostKey(host);
                item.Tag = host;
                item.SubItems.Add(host.HostAddress);
                item.SubItems.Add(host.HostPort.ToString());
                item.SubItems.Add(seenSeconds + "s");
                _hostsListView.Items.Add(item);

                if (!string.IsNullOrEmpty(selectedKey) && string.Equals(selectedKey, item.Name, StringComparison.Ordinal))
                {
                    item.Selected = true;
                }
            }

            _hostsListView.EndUpdate();
        }

        private static string BuildHostKey(DiscoveredHostInfo host)
        {
            return host.HostAddress + ":" + host.HostPort;
        }

        private static string GetHostDisplayName(DiscoveredHostInfo host)
        {
            if (!string.IsNullOrWhiteSpace(host.DisplayName))
            {
                return host.DisplayName;
            }

            if (!string.IsNullOrWhiteSpace(host.MachineName))
            {
                return host.MachineName;
            }

            return host.HostAddress;
        }

        private bool TryTranslatePoint(Point localPoint, out Point remotePoint)
        {
            remotePoint = Point.Empty;

            if (_remoteSize.Width <= 0 || _remoteSize.Height <= 0 || _pictureBox.Width <= 0 || _pictureBox.Height <= 0)
            {
                return false;
            }

            if (localPoint.X < 0 || localPoint.Y < 0 || localPoint.X >= _pictureBox.Width || localPoint.Y >= _pictureBox.Height)
            {
                return false;
            }

            var xRatio = (double)localPoint.X / _pictureBox.Width;
            var yRatio = (double)localPoint.Y / _pictureBox.Height;

            var remoteX = Math.Max(0, Math.Min(_remoteSize.Width - 1, (int)Math.Round(xRatio * (_remoteSize.Width - 1))));
            var remoteY = Math.Max(0, Math.Min(_remoteSize.Height - 1, (int)Math.Round(yRatio * (_remoteSize.Height - 1))));

            remotePoint = new Point(remoteX, remoteY);
            return true;
        }

        private void ImagePanel_Resize(object sender, EventArgs e)
        {
            UpdateImageLayout();
        }

        private void ApplyZoom(double zoomFactor)
        {
            _zoomFactor = Math.Max(MinZoom, Math.Min(MaxZoom, zoomFactor));
            UpdateImageLayout();
        }

        private void UpdateImageLayout()
        {
            if (_currentFrame == null || _remoteSize.Width <= 0 || _remoteSize.Height <= 0)
            {
                _zoomLabel.Text = "Zoom: Fit";
                return;
            }

            var viewportWidth = Math.Max(1, _imagePanel.ClientSize.Width);
            var viewportHeight = Math.Max(1, _imagePanel.ClientSize.Height);
            var fitScale = Math.Min((double)viewportWidth / _remoteSize.Width, (double)viewportHeight / _remoteSize.Height);
            fitScale = Math.Min(1.0, fitScale);

            var displayScale = fitScale * _zoomFactor;
            var displayWidth = Math.Max(1, (int)Math.Round(_remoteSize.Width * displayScale));
            var displayHeight = Math.Max(1, (int)Math.Round(_remoteSize.Height * displayScale));

            _pictureBox.Size = new Size(displayWidth, displayHeight);
            _pictureBox.Location = new Point(
                Math.Max(0, (viewportWidth - displayWidth) / 2),
                Math.Max(0, (viewportHeight - displayHeight) / 2));
            _zoomLabel.Text = "Zoom: " + (int)Math.Round(_zoomFactor * 100) + "%";
        }

        private static MouseButtonCode TranslateButton(MouseButtons button)
        {
            switch (button)
            {
                case MouseButtons.Right:
                    return MouseButtonCode.Right;
                case MouseButtons.Middle:
                    return MouseButtonCode.Middle;
                default:
                    return MouseButtonCode.Left;
            }
        }
    }
}
