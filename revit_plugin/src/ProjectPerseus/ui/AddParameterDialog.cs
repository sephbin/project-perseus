using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProjectPerseus.mapping;
using ProjectPerseus.models;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// "Add local parameter" for a parameter mapping (ui/FamilyMappingWindow): name, data type (the consultant's, or text),
    /// instance / type, and the optional company shared parameter file. The work is mapping/ParamCreator.
    /// </summary>
    internal class AddParameterDialog : Window
    {
        private const string TextSpec = "autodesk.spec:spec.string-2.0.0";
        private readonly TextBox _name;
        private readonly ComboBox _type;
        private readonly RadioButton _instance, _typeParam;
        private readonly TextBlock _company;
        private readonly ParamRef _src;

        internal string ParamName => _name.Text?.Trim();
        internal bool IsTypeParam => _typeParam.IsChecked == true;
        /// <summary>Spec id of the new parameter; null = text (mapping/ParamCreator falls back to text).</summary>
        internal string Spec => _type.SelectedIndex == 0 && _src.Kind != ParamCompat.Text && _src.Kind != ParamCompat.Element ? _src.Spec : null;

        internal AddParameterDialog(ParamRef src, string category)
        {
            _src = src;
            Title = "Perseus — Add local parameter";
            Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 12;
            var muted = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));

            var root = new StackPanel { Margin = new Thickness(14) };
            root.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Foreground = muted, Margin = new Thickness(0, 0, 0, 10),
                Text = $"Adds a project parameter on {category} (shared, group Other) for the consultant's '{src.Name}'. If the model already has a " +
                       "parameter of that name with a fitting data type, that one is used instead.",
            });
            root.Children.Add(new TextBlock { Text = "Name", FontWeight = FontWeights.SemiBold });
            _name = new TextBox { Text = src.Name, Margin = new Thickness(0, 2, 0, 8) };
            root.Children.Add(_name);

            root.Children.Add(new TextBlock { Text = "Data type", FontWeight = FontWeights.SemiBold });
            _type = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
            bool textOnly = src.Kind == ParamCompat.Text || src.Kind == ParamCompat.Element;
            _type.Items.Add(textOnly ? "Text" : $"Same as the consultant's ({src.SpecLabel ?? src.Kind})");
            if (!textOnly) _type.Items.Add("Text");
            _type.SelectedIndex = 0;
            root.Children.Add(_type);

            root.Children.Add(new TextBlock { Text = "Kind", FontWeight = FontWeights.SemiBold });
            var kind = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            _instance = new RadioButton { Content = "Instance (a value per family)", Margin = new Thickness(0, 0, 16, 0), IsChecked = !src.IsType };
            _typeParam = new RadioButton { Content = "Type (one value per family type)", IsChecked = src.IsType };
            kind.Children.Add(_instance); kind.Children.Add(_typeParam);
            root.Children.Add(kind);
            root.Children.Add(new TextBlock
            {
                Text = "Revit can't switch a parameter between instance and type later without losing its values.",
                Foreground = muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            });

            root.Children.Add(new TextBlock { Text = "Shared parameter file", FontWeight = FontWeights.SemiBold });
            _company = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = muted, FontSize = 11, Margin = new Thickness(0, 2, 0, 4) };
            root.Children.Add(_company);
            var fileRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var choose = new Button { Content = "Company file…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0),
                                      ToolTip = "Use definitions from your company's shared parameter file when it has one of that name (yours only; IT can set it for everyone)" };
            var clear = new Button { Content = "Perseus only", Padding = new Thickness(8, 2, 8, 2) };
            choose.Click += (s, e) => Pick();
            clear.Click += (s, e) => { ParamCreator.CompanyFile = null; ShowCompany(); };
            fileRow.Children.Add(choose); fileRow.Children.Add(clear);
            root.Children.Add(fileRow);
            ShowCompany();

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "Add", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 6, 0) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
            ok.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(_name.Text)) { MessageBox.Show(this, "Give the parameter a name.", "Add local parameter"); return; }
                DialogResult = true;
            };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            Content = root;
            Loaded += (s, e) => { _name.Focus(); _name.SelectAll(); };
        }

        private void ShowCompany()
        {
            string f = ParamCreator.CompanyFile;
            _company.Text = f == null
                ? "Perseus file (your AppData). Its definitions are generated the same for everyone (GUID from name + data type)."
                : $"Company file first: {f}\nNames not in it go to the Perseus file.";
        }

        private void Pick()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Shared parameter files (*.txt)|*.txt|All files (*.*)|*.*", Title = "Company shared parameter file" };
            if (dlg.ShowDialog(this) == true) { ParamCreator.CompanyFile = dlg.FileName; ShowCompany(); }
        }
    }
}
