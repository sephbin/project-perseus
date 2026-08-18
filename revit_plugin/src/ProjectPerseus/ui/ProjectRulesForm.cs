using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProjectPerseus.auth;
using ProjectPerseus.logging;
using ProjectPerseus.web;

namespace ProjectPerseus.ui
{
    internal class ProjectRulesForm : Form
    {
        private readonly string _sourceGuid;
        private readonly string _baseUrl;
        private string _sessionToken;
        private DateTime _sessionExpiry = DateTime.MinValue;
        private JArray _cachedFilters = new JArray();

        // Source Info tab
        private Label _lblNameVal;
        private Label _lblGuidVal;
        private Label _lblMediumVal;
        private DataGridView _paramsGrid;

        // Violation Rules tab
        private ComboBox _cboDefaultEdit;
        private ComboBox _cboDefaultSync;
        private DataGridView _enforcementGrid;
        private TextBox _txtProtectedIds;
        private CheckedListBox _clbCategories;
        private CheckBox _chkBypassApproval;
        private Label _lblUpdatedAt;

        // Bottom bar
        private Label _lblStatus;
        private Button _btnSave;
        private Button _btnRefresh;

        private static readonly (string Key, string Label)[] ActionTypes =
        {
            ("element_deletion", "Element Deletion"),
            ("ungroup",          "Ungroup"),
            ("link_unload",      "Link Unload"),
            ("warnings",         "Warnings"),
            ("unpin",            "Unpin"),
        };

        private static readonly string[] OnEditOptions = { "inherit", "log", "dialog" };
        private static readonly string[] OnSyncOptions = { "inherit", "log", "warn", "block" };

        private static readonly string[] AllCategories =
        {
            "Air Terminals", "Areas", "Cable Tray Fittings", "Cable Trays", "Casework",
            "Ceilings", "Communication Devices", "Conduit Fittings", "Conduits",
            "Curtain Panels", "Curtain Systems", "Curtain Wall Mullions", "Data Devices",
            "Detail Items", "Doors", "Duct Accessories", "Duct Fittings", "Duct Insulations",
            "Duct Linings", "Ducts", "Electrical Equipment", "Electrical Fixtures",
            "Entourage", "Fire Alarm Devices", "Flex Ducts", "Flex Pipes", "Floors",
            "Furniture", "Furniture Systems", "Generic Models", "Grids", "Levels",
            "Lighting Devices", "Lighting Fixtures", "Mass", "Mechanical Equipment",
            "MEP Fabrication Containment", "MEP Fabrication Ductwork",
            "MEP Fabrication Hangers", "MEP Fabrication Pipework", "Model Groups",
            "Nurse Call Devices", "Parking", "Pipe Accessories", "Pipe Fittings",
            "Pipe Insulations", "Pipes", "Planting", "Plumbing Fixtures",
            "Property Lines", "Railings", "Ramps", "Revision Clouds", "Roads", "Roofs",
            "Rooms", "Security Devices", "Site", "Spaces", "Specialty Equipment",
            "Sprinklers", "Stairs", "Structural Beam Systems", "Structural Columns",
            "Structural Connections", "Structural Foundations", "Structural Framing",
            "Structural Path Reinforcement", "Structural Rebar", "Structural Stiffeners",
            "Structural Trusses", "Telephone Devices", "Topography", "Walls", "Windows",
            "Wire", "Zones",
        };

        public ProjectRulesForm(string sourceGuid, string baseUrl)
        {
            _sourceGuid = sourceGuid;
            _baseUrl = baseUrl.TrimEnd('/');
            Build();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Task.Run(() => LoadDataBackground());
        }

        // ── Form construction ───────────────────────────────────────────────

        private void Build()
        {
            Text = "Perseus — Project Rules";
            Size = new Size(920, 720);
            MinimumSize = new Size(720, 520);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8, 7, 8, 0),
            };
            var btnClose = new Button { Text = "Close", Width = 80, Height = 30 };
            btnClose.Click += (s, ev) => Close();

            _btnSave = new Button
            {
                Text = "Save to Server",
                Width = 130, Height = 30,
                BackColor = Color.FromArgb(26, 26, 46),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
            };
            _btnSave.Click += BtnSave_Click;

            _btnRefresh = new Button { Text = "Refresh", Width = 80, Height = 30 };
            _btnRefresh.Click += (s, ev) => Task.Run(() => LoadDataBackground());

            bottom.Controls.AddRange(new Control[] { btnClose, _btnSave, _btnRefresh });

            var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 22 };
            _lblStatus = new Label { Text = "Loading...", AutoSize = true, ForeColor = Color.DimGray, Left = 10, Top = 3 };
            statusBar.Controls.Add(_lblStatus);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            var pageSource = new TabPage("Source Info");
            pageSource.Controls.Add(BuildSourceInfoPanel());
            var pageRules = new TabPage("Violation Rules");
            pageRules.Controls.Add(BuildViolationRulesPanel());
            tabs.TabPages.AddRange(new[] { pageSource, pageRules });

            Controls.AddRange(new Control[] { tabs, statusBar, bottom });
        }

        // ── Source Info tab ─────────────────────────────────────────────────

        private Control BuildSourceInfoPanel()
        {
            var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            const int w = 856;

            _lblNameVal   = new Label { Left = 80, Top = 24, Width = 740, AutoSize = false, Text = "—" };
            _lblGuidVal   = new Label { Left = 80, Top = 50, Width = 740, AutoSize = false, Text = "—",
                                        Font = new Font("Consolas", 8.5f), ForeColor = Color.DimGray };
            _lblMediumVal = new Label { Left = 80, Top = 76, Width = 300, AutoSize = false, Text = "—" };

            var grpInfo = new GroupBox { Text = "Source", Left = 8, Top = 8, Width = w, Height = 110 };
            grpInfo.Controls.AddRange(new Control[]
            {
                new Label { Text = "Name:",   Left = 12, Top = 24, AutoSize = true },
                new Label { Text = "GUID:",   Left = 12, Top = 50, AutoSize = true },
                new Label { Text = "Medium:", Left = 12, Top = 76, AutoSize = true },
                _lblNameVal, _lblGuidVal, _lblMediumVal,
            });

            var grpParams = new GroupBox { Text = "Source Parameters", Left = 8, Top = 126, Width = w, Height = 320 };
            _paramsGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.None,
            };
            _paramsGrid.Columns.AddRange(
                new DataGridViewTextBoxColumn { Name = "pName",  HeaderText = "Name",  FillWeight = 35 },
                new DataGridViewTextBoxColumn { Name = "pValue", HeaderText = "Value", FillWeight = 65 });
            grpParams.Controls.Add(_paramsGrid);

            panel.Controls.AddRange(new Control[] { grpInfo, grpParams });
            return panel;
        }

        // ── Violation Rules tab ─────────────────────────────────────────────

        private Control BuildViolationRulesPanel()
        {
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            const int w = 856;
            int x = 8, y = 8;

            // Default Enforcement
            _cboDefaultEdit = BuildDropDown(130, 20, new[] { "log", "dialog" });
            _cboDefaultSync = BuildDropDown(350, 20, new[] { "log", "warn", "block" });

            var grpDef = new GroupBox { Text = "Default Enforcement", Left = x, Top = y, Width = w, Height = 80 };
            grpDef.Controls.AddRange(new Control[]
            {
                new Label { Text = "On Edit:", Left = 12, Top = 24, AutoSize = true },
                _cboDefaultEdit,
                new Label { Text = "On Sync:", Left = 262, Top = 24, AutoSize = true },
                _cboDefaultSync,
            });
            y += 88;

            // Per-Action Enforcement
            int gridRows  = ActionTypes.Length;
            int gridH     = 24 + gridRows * 24 + 4;
            var grpEnf = new GroupBox { Text = "Per-Action Enforcement", Left = x, Top = y, Width = w, Height = gridH + 28 };

            _enforcementGrid = new DataGridView
            {
                Left = 8, Top = 22, Width = w - 18, Height = gridH,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                RowHeadersVisible = false,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.None,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowTemplate = { Height = 24 },
            };
            var colAction = new DataGridViewTextBoxColumn { Name = "action", HeaderText = "Action Type", FillWeight = 40, ReadOnly = true };
            var colEdit = new DataGridViewComboBoxColumn
            {
                Name = "onEdit", HeaderText = "On Edit", FillWeight = 30,
                FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
            };
            colEdit.Items.AddRange(OnEditOptions.Cast<object>().ToArray());
            var colSync = new DataGridViewComboBoxColumn
            {
                Name = "onSync", HeaderText = "On Sync", FillWeight = 30,
                FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
            };
            colSync.Items.AddRange(OnSyncOptions.Cast<object>().ToArray());
            _enforcementGrid.Columns.AddRange(colAction, colEdit, colSync);
            _enforcementGrid.DataError += (s, e) => e.Cancel = true;

            foreach (var (key, label) in ActionTypes)
            {
                int i = _enforcementGrid.Rows.Add(label, "inherit", "inherit");
                _enforcementGrid.Rows[i].Tag = key;
            }
            grpEnf.Controls.Add(_enforcementGrid);
            y += grpEnf.Height + 8;

            // Protected Element IDs
            var grpIds = new GroupBox
            {
                Text = "Protected Element IDs  (one UniqueId or ElementId per line)",
                Left = x, Top = y, Width = w, Height = 118,
            };
            _txtProtectedIds = new TextBox
            {
                Multiline = true, ScrollBars = ScrollBars.Vertical,
                Left = 8, Top = 22, Width = w - 18, Height = 86,
                Font = new Font("Consolas", 8.5f),
            };
            grpIds.Controls.Add(_txtProtectedIds);
            y += 126;

            // Protected Categories
            var grpCats = new GroupBox { Text = "Protected Categories", Left = x, Top = y, Width = w, Height = 178 };
            _clbCategories = new CheckedListBox
            {
                Left = 8, Top = 22, Width = w - 18, Height = 146,
                CheckOnClick = true,
            };
            _clbCategories.Items.AddRange(AllCategories.Cast<object>().ToArray());
            grpCats.Controls.Add(_clbCategories);
            y += 186;

            // Bypass Approval
            var grpBypass = new GroupBox { Text = "Bypass Approval", Left = x, Top = y, Width = w, Height = 68 };
            _chkBypassApproval = new CheckBox
            {
                Text = "Require approval to bypass violation blocks",
                Left = 12, Top = 22, AutoSize = true,
            };
            _lblUpdatedAt = new Label { Left = 12, Top = 46, AutoSize = true, ForeColor = Color.DimGray };
            grpBypass.Controls.AddRange(new Control[] { _chkBypassApproval, _lblUpdatedAt });
            y += 76;

            scroll.AutoScrollMinSize = new Size(0, y + 8);
            scroll.Controls.AddRange(new Control[] { grpDef, grpEnf, grpIds, grpCats, grpBypass });
            return scroll;
        }

        private static ComboBox BuildDropDown(int left, int top, string[] items)
        {
            var cbo = new ComboBox { Left = left, Top = top, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
            cbo.Items.AddRange(items.Cast<object>().ToArray());
            cbo.SelectedIndex = 0;
            return cbo;
        }

        // ── Data loading ────────────────────────────────────────────────────

        private void LoadDataBackground()
        {
            SetStatus("Loading...", Color.DimGray);
            SetEnabled(false);

            try
            {
                // Source info requires auth; violation settings are public GET
                string token  = AuthService.GetAuthTokenSafely();
                string scheme = AuthService.GetAuthSchemeSafely();

                JObject sourceData = null;
                try
                {
                    string src = WebHelper.Get(
                        $"{_baseUrl}/rapi/sources/?unique_id={_sourceGuid}", token, null, scheme);
                    var parsed = JToken.Parse(src);
                    JArray arr = parsed as JArray
                              ?? (parsed is JObject o ? o["results"] as JArray : null);
                    if (arr != null && arr.Count > 0)
                        sourceData = arr[0] as JObject;
                }
                catch (Exception ex)
                {
                    Log.Info($"[ProjectRulesForm] source info fetch failed: {ex.Message}");
                }

                JObject vsData;
                try
                {
                    string vsJson = WebHelper.Get(
                        $"{_baseUrl}/api/source/{_sourceGuid}/violation-settings/", null, null);
                    vsData = JObject.Parse(vsJson);
                }
                catch (Exception ex)
                {
                    Log.Error($"[ProjectRulesForm] violation settings fetch failed: {ex.Message}");
                    SetStatus($"Failed to load settings: {ex.Message}", Color.DarkRed);
                    SetEnabled(true);
                    return;
                }

                InvokeIfAlive(() =>
                {
                    if (sourceData != null)
                    {
                        _lblNameVal.Text   = sourceData["name"]?.ToString() ?? _sourceGuid;
                        _lblGuidVal.Text   = _sourceGuid;
                        _lblMediumVal.Text = sourceData["medium"]?.ToString() ?? "—";
                        _paramsGrid.Rows.Clear();
                        if (sourceData["parameter_dict"] is JObject pd)
                            foreach (var p in pd.Properties())
                                _paramsGrid.Rows.Add(p.Name, p.Value?.ToString() ?? "");
                    }
                    else
                    {
                        _lblNameVal.Text = "(not yet registered on server)";
                        _lblGuidVal.Text = _sourceGuid;
                    }

                    PopulateViolationSettings(vsData);
                    SetStatus("Ready.", Color.DimGray);
                    SetEnabled(true);
                });
            }
            catch (Exception ex)
            {
                Log.Error($"[ProjectRulesForm] load failed: {ex.Message}");
                SetStatus($"Error: {ex.Message}", Color.DarkRed);
                SetEnabled(true);
            }
        }

        private void PopulateViolationSettings(JObject vs)
        {
            SelectComboItem(_cboDefaultEdit, vs["default_on_edit_style"]?.ToString(), "log");
            SelectComboItem(_cboDefaultSync, vs["default_on_sync_style"]?.ToString(), "warn");

            foreach (DataGridViewRow row in _enforcementGrid.Rows)
            {
                if (!(row.Tag is string key)) continue;
                row.Cells["onEdit"].Value = Coerce(vs[$"enforce_{key}_edit"]?.ToString(),  OnEditOptions, "inherit");
                row.Cells["onSync"].Value = Coerce(vs[$"enforce_{key}_sync"]?.ToString(), OnSyncOptions, "inherit");
            }

            var ids = (vs["protected_element_ids"] as JArray)
                          ?.Select(t => t.ToString()).ToArray() ?? Array.Empty<string>();
            _txtProtectedIds.Lines = ids;

            var cats = (vs["protected_categories"] as JArray)
                           ?.Select(t => t.ToString()).ToHashSet() ?? new HashSet<string>();
            for (int i = 0; i < _clbCategories.Items.Count; i++)
                _clbCategories.SetItemChecked(i, cats.Contains(_clbCategories.Items[i].ToString()));

            _chkBypassApproval.Checked = vs["bypass_requires_approval"]?.Value<bool>() ?? true;

            string ua = vs["updated_at"]?.ToString();
            _lblUpdatedAt.Text = string.IsNullOrEmpty(ua) ? "" : $"Last updated: {ua}";

            _cachedFilters = (vs["protected_filters"] as JArray) ?? new JArray();
        }

        private static void SelectComboItem(ComboBox cbo, string value, string fallback)
        {
            cbo.SelectedItem = value;
            if (cbo.SelectedIndex < 0) cbo.SelectedItem = fallback;
            if (cbo.SelectedIndex < 0 && cbo.Items.Count > 0) cbo.SelectedIndex = 0;
        }

        private static string Coerce(string value, string[] options, string fallback)
            => options.Contains(value) ? value : fallback;

        // ── Save to Server ──────────────────────────────────────────────────

        private void BtnSave_Click(object sender, EventArgs e)
        {
            _btnSave.Enabled    = false;
            _btnRefresh.Enabled = false;

            // Snapshot the payload on the UI thread before handing off to background
            JObject payload = BuildPutPayload();
            Task.Run(() => SaveBackground(payload));
        }

        private void SaveBackground(JObject payload)
        {
            try
            {
                if (string.IsNullOrEmpty(_sessionToken) || DateTime.UtcNow >= _sessionExpiry)
                {
                    string token = AcquireSessionTokenBackground();
                    if (token == null)
                    {
                        InvokeIfAlive(() => { _btnSave.Enabled = true; _btnRefresh.Enabled = true; });
                        return;
                    }
                    _sessionToken  = token;
                    _sessionExpiry = DateTime.UtcNow.AddHours(8);
                }

                SetStatus("Saving...", Color.DimGray);
                string endpoint = $"{_baseUrl}/api/source/{_sourceGuid}/violation-settings/";
                WebHelper.Put(endpoint, _sessionToken, payload.ToString(Formatting.None), "PerseusSession");

                Log.Info($"[ProjectRulesForm] violation settings saved for {_sourceGuid}");
                SetStatus("Saved. Refreshing...", Color.DarkGreen);
                LoadDataBackground();
            }
            catch (Exception ex)
            {
                Log.Error($"[ProjectRulesForm] save failed: {ex.Message}");
                SetStatus($"Save failed: {ex.Message}", Color.DarkRed);
                InvokeIfAlive(() => { _btnSave.Enabled = true; _btnRefresh.Enabled = true; });
            }
        }

        private string AcquireSessionTokenBackground()
        {
            SetStatus("Initiating browser authorization...", Color.DimGray);

            string code, pairUrl;
            try
            {
                string body = JsonConvert.SerializeObject(new { source_guid = _sourceGuid });
                string initJson = WebHelper.Post($"{_baseUrl}/api/auth/pair/initiate/", null, body);
                var initData = JObject.Parse(initJson);
                code    = initData["code"]?.ToString();
                pairUrl = initData["pair_url"]?.ToString();
            }
            catch (Exception ex)
            {
                SetStatus($"Could not initiate authorization: {ex.Message}", Color.DarkRed);
                return null;
            }

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(pairUrl))
            {
                SetStatus("Invalid pairing response from server.", Color.DarkRed);
                return null;
            }

            Process.Start(new ProcessStartInfo(pairUrl) { UseShellExecute = true });
            SetStatus("Opened browser — please authorize then return here.", Color.DimGray);

            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(2500);
                try
                {
                    string statusJson = WebHelper.Get(
                        $"{_baseUrl}/api/auth/pair/{code}/status/", null, null);
                    var statusData = JObject.Parse(statusJson);
                    string status  = statusData["status"]?.ToString();
                    string token   = statusData["session_token"]?.ToString();

                    if (status == "authenticated" && !string.IsNullOrEmpty(token))
                    {
                        SetStatus("Authorized.", Color.DarkGreen);
                        return token;
                    }
                    if (status == "expired")
                    {
                        SetStatus("Authorization request expired. Please try again.", Color.DarkRed);
                        return null;
                    }
                }
                catch
                {
                    // transient — keep polling
                }

                int remaining = Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalSeconds);
                SetStatus($"Waiting for browser authorization... ({remaining}s)", Color.DimGray);
            }

            SetStatus("Authorization timed out. Please try again.", Color.DarkRed);
            return null;
        }

        // ── Build PUT payload ───────────────────────────────────────────────

        private JObject BuildPutPayload()
        {
            var p = new JObject();
            p["default_on_edit_style"] = _cboDefaultEdit.SelectedItem?.ToString() ?? "log";
            p["default_on_sync_style"] = _cboDefaultSync.SelectedItem?.ToString() ?? "warn";

            foreach (DataGridViewRow row in _enforcementGrid.Rows)
            {
                if (!(row.Tag is string key)) continue;
                p[$"enforce_{key}_edit"] = row.Cells["onEdit"].Value?.ToString() ?? "inherit";
                p[$"enforce_{key}_sync"] = row.Cells["onSync"].Value?.ToString() ?? "inherit";
            }

            p["protected_element_ids"] = JArray.FromObject(
                _txtProtectedIds.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList());

            p["protected_categories"] = JArray.FromObject(
                _clbCategories.CheckedItems.Cast<string>().ToList());

            p["protected_filters"]       = _cachedFilters;
            p["bypass_requires_approval"] = _chkBypassApproval.Checked;

            return p;
        }

        // ── UI helpers ───────────────────────────────────────────────────────

        private void SetStatus(string text, Color color)
        {
            if (InvokeRequired) { Invoke(new Action<string, Color>(SetStatus), text, color); return; }
            _lblStatus.Text      = text;
            _lblStatus.ForeColor = color;
        }

        private void SetEnabled(bool enabled)
        {
            if (InvokeRequired) { Invoke(new Action<bool>(SetEnabled), enabled); return; }
            _btnSave.Enabled    = enabled;
            _btnRefresh.Enabled = enabled;
        }

        private void InvokeIfAlive(Action action)
        {
            if (!IsDisposed && IsHandleCreated)
                try { Invoke(action); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
        }
    }
}
