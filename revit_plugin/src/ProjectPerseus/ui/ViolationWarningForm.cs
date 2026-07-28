using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ProjectPerseus.violations;

namespace ProjectPerseus.ui
{
    internal class ViolationWarningForm : Form
    {
        private readonly List<ViolationElementInfo> _elements;
        private readonly string _actionVerb;

        // Populated on OK — element IDs the user approved for the action to proceed.
        internal HashSet<long> ApprovedElementIds { get; private set; } = new HashSet<long>();

        private TreeView _tree;
        private Font _boldFont;
        private bool _handlingCheck;

        internal ViolationWarningForm(List<ViolationElementInfo> elements, string actionVerb = "delete")
        {
            _elements   = elements;
            _actionVerb = actionVerb;
            InitializeUI();
            PopulateTree();
        }

        private void InitializeUI()
        {
            Text            = "Perseus — Protected Element Warning";
            Size            = new Size(700, 560);
            MinimumSize     = new Size(480, 380);
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;

            _boldFont = new Font(Font, FontStyle.Bold);

            int protectedCount = _elements.Count(e => e.IsProtected);
            int totalCount     = _elements.Count;

            var lblHeader = new Label
            {
                Text      = $"You are about to {_actionVerb} {totalCount} element(s). " +
                            $"{protectedCount} are protected (shown in red).\r\n" +
                            $"Check elements to proceed. Unchecked elements will be restored.",
                Dock      = DockStyle.Top,
                Height    = 50,
                Padding   = new Padding(8, 8, 8, 0),
                AutoSize  = false,
            };

            var pnlButtons = new FlowLayoutPanel
            {
                Dock      = DockStyle.Top,
                Height    = 36,
                Padding   = new Padding(6, 4, 0, 0),
                AutoSize  = false,
            };
            var btnSelectAll  = new Button { Text = "Select All",  AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
            var btnSelectNone = new Button { Text = "Select None", AutoSize = true };
            btnSelectAll.Click  += (s, e) => SetAllChecked(true);
            btnSelectNone.Click += (s, e) => SetAllChecked(false);
            pnlButtons.Controls.AddRange(new Control[] { btnSelectAll, btnSelectNone });

            _tree = new TreeView
            {
                Dock       = DockStyle.Fill,
                CheckBoxes = true,
                HideSelection = false,
            };
            _tree.AfterCheck += OnAfterCheck;

            var pnlBottom = new Panel { Dock = DockStyle.Bottom, Height = 44 };
            var btnOk     = new Button { Text = "OK",     Size = new Size(90, 28), DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "Cancel", Size = new Size(90, 28), DialogResult = DialogResult.Cancel };
            pnlBottom.Controls.AddRange(new Control[] { btnOk, btnCancel });
            pnlBottom.Layout += (s, e) =>
            {
                int totalW  = btnOk.Width + 8 + btnCancel.Width;
                int startX  = (pnlBottom.ClientSize.Width - totalW) / 2;
                btnOk.SetBounds(startX, 8, 90, 28);
                btnCancel.SetBounds(startX + 98, 8, 90, 28);
            };

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            // Dock order: Bottom first, then Tops stack top-to-bottom in reverse add order.
            Controls.Add(_tree);
            Controls.Add(pnlButtons);
            Controls.Add(lblHeader);
            Controls.Add(pnlBottom);

            FormClosing += OnFormClosing;
        }

        private void PopulateTree()
        {
            _tree.BeginUpdate();

            var byCategory = _elements.GroupBy(e => e.CategoryName ?? "(No Category)")
                                      .OrderBy(g => g.Key);

            foreach (var catGroup in byCategory)
            {
                var catNode = new TreeNode($"{catGroup.Key}  ({catGroup.Count()})");

                var byFamilyType = catGroup.GroupBy(e =>
                        string.IsNullOrEmpty(e.FamilyTypeName) ? "(Unknown Type)" : e.FamilyTypeName)
                    .OrderBy(g => g.Key);

                foreach (var ftGroup in byFamilyType)
                {
                    var ftNode = new TreeNode($"{ftGroup.Key}  ({ftGroup.Count()})");

                    foreach (var el in ftGroup.OrderBy(e => e.ElementName))
                    {
                        string label = string.IsNullOrEmpty(el.ElementName)
                            ? $"ID {el.ElementId}"
                            : el.ElementName;

                        var elNode = new TreeNode(label) { Tag = el, Checked = false };

                        if (el.IsProtected)
                        {
                            elNode.ForeColor = Color.DarkRed;
                            elNode.NodeFont  = _boldFont;
                            label += "  ★ protected";
                            elNode.Text = label;
                        }

                        ftNode.Nodes.Add(elNode);
                    }

                    catNode.Nodes.Add(ftNode);
                }

                _tree.Nodes.Add(catNode);
            }

            _tree.ExpandAll();
            _tree.EndUpdate();
        }

        private void OnAfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_handlingCheck || e.Action == TreeViewAction.Unknown) return;
            _handlingCheck = true;
            try
            {
                PropagateDown(e.Node, e.Node.Checked);
                PropagateUp(e.Node.Parent);
            }
            finally { _handlingCheck = false; }
        }

        private void PropagateDown(TreeNode node, bool isChecked)
        {
            foreach (TreeNode child in node.Nodes)
            {
                child.Checked = isChecked;
                PropagateDown(child, isChecked);
            }
        }

        private void PropagateUp(TreeNode parent)
        {
            if (parent == null) return;
            bool allChecked = parent.Nodes.Cast<TreeNode>().All(n => n.Checked);
            parent.Checked  = allChecked;
            PropagateUp(parent.Parent);
        }

        private void SetAllChecked(bool isChecked)
        {
            _handlingCheck = true;
            try { foreach (TreeNode n in _tree.Nodes) SetCheckedRecursive(n, isChecked); }
            finally { _handlingCheck = false; }
        }

        private void SetCheckedRecursive(TreeNode node, bool isChecked)
        {
            node.Checked = isChecked;
            foreach (TreeNode child in node.Nodes) SetCheckedRecursive(child, isChecked);
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (DialogResult != DialogResult.OK) return;
            ApprovedElementIds = new HashSet<long>();
            CollectApproved(_tree.Nodes);
        }

        private void CollectApproved(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                if (node.Tag is ViolationElementInfo info && node.Checked)
                    ApprovedElementIds.Add(info.ElementId);
                CollectApproved(node.Nodes);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _boldFont?.Dispose();
            base.Dispose(disposing);
        }
    }
}
