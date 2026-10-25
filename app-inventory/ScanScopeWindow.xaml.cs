using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;

namespace AppInventory;

public partial class ScanScopeWindow : Window
{
    private readonly ObservableCollection<string> _roots;
    private readonly ObservableCollection<string> _exclusions;
    internal IReadOnlyList<string> ExtraRoots => _roots.ToList();
    internal IReadOnlyList<string> Exclusions => _exclusions.ToList();
    internal bool AllFixed => FixedCheck.IsChecked == true;
    internal ScanScope Scope => new(PortableScanner.NormalizeRoots((AllFixed ? PortableScanner.FixedDrives() : new List<string>()).Concat(_roots)), _exclusions.ToList());

    internal ScanScopeWindow(Window owner, IEnumerable<string> roots, IEnumerable<string> exclusions, bool allFixed)
    {
        _roots = new ObservableCollection<string>(roots);
        _exclusions = new ObservableCollection<string>(exclusions);
        InitializeComponent();
        Owner = owner;
        Resources = owner.Resources;
        RootList.ItemsSource = _roots;
        ExclusionList.ItemsSource = _exclusions;
        FixedCheck.IsChecked = allFixed;
        UpdateSummary();
    }

    private void Scope_Changed(object sender, RoutedEventArgs e) { if (ScopeSummary != null && StartButton != null) UpdateSummary(); }
    private void UpdateSummary()
    {
        var scope = Scope;
        ScopeSummary.Text = scope.Roots.Count == 0 ? "尚未选择扫描范围。" : "将扫描：" + string.Join("、", scope.Roots);
        StartButton.IsEnabled = scope.Roots.Count > 0;
    }

    private void AddFolders(ObservableCollection<string> target, string title)
    {
        var picker = new OpenFolderDialog { Title = title, Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        foreach (var path in picker.FolderNames)
            if (!target.Contains(path, StringComparer.OrdinalIgnoreCase)) target.Add(path);
        UpdateSummary();
    }

    private void AddRoot_Click(object sender, RoutedEventArgs e) => AddFolders(_roots, "选择要识别便携软件及关联位置的目录");
    private void AddExclusion_Click(object sender, RoutedEventArgs e) => AddFolders(_exclusions, "选择要排除的目录，也可以排除整个磁盘");
    private void RemoveRoot_Click(object sender, RoutedEventArgs e) { if (RootList.SelectedItem is string path) _roots.Remove(path); UpdateSummary(); }
    private void RemoveExclusion_Click(object sender, RoutedEventArgs e) { if (ExclusionList.SelectedItem is string path) _exclusions.Remove(path); }
    private void Start_Click(object sender, RoutedEventArgs e) { if (Scope.Roots.Count > 0) DialogResult = true; }
}
