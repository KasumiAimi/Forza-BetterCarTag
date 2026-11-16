using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace FH6GarageEditor;

public partial class TagManagerWindow : Window
{
    private sealed class Draft(TagDefinition tag) : INotifyPropertyChanged
    {
        public string Name { get; set; } = tag.Name;
        public string Color { get; private set; } = tag.Color;
        public event PropertyChangedEventHandler? PropertyChanged;

        internal TagDefinition Definition => tag with { Name = Name, Color = Color };

        internal void SetColor(string color)
        {
            Color = color;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Color)));
        }
    }

    private sealed class DialogOwner(IntPtr handle) : Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    private const string DeleteNotice = "保存后，删除的标签会从所有车库档案的车辆上移除。";
    private readonly ObservableCollection<Draft>[] drafts;
    private readonly string file;
    internal TagCatalog Catalog { get; private set; }

    internal TagManagerWindow(TagCatalog catalog, string file)
    {
        Catalog = catalog;
        this.file = file;
        drafts = TagCatalog.Categories.Select((_, index) =>
            new ObservableCollection<Draft>(catalog[index].Select(tag => new Draft(tag)))).ToArray();
        InitializeComponent();
        ShowCategory();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        MainWindow.UseDarkCaption(this);
    }

    private void CategoryChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ReferenceEquals(args.Source, CategoryTabs) && TagRows is not null) ShowCategory();
    }

    private void ShowCategory()
    {
        int index = CategoryTabs.SelectedIndex;
        if (index < 0) return;
        TagRows.ItemsSource = drafts[index];
        TagScroll.ScrollToTop();
        int limit = TagCatalog.Categories[index].MaxWidth;
        LengthHint.Text = $"最多 {limit / 2} 个汉字或 {limit} 个英文字母。混用时，非 ASCII 字符按 2 个计。";
        Notice.Text = DeleteNotice;
    }

    private void AddTag(object sender, RoutedEventArgs args)
    {
        var draft = new Draft(new TagDefinition(Guid.NewGuid().ToString("N"), "", "#0397FC"));
        drafts[CategoryTabs.SelectedIndex].Add(draft);
        TagRows.UpdateLayout();
        if (TagRows.ItemContainerGenerator.ContainerFromItem(draft) is ContentPresenter row &&
            row.ContentTemplate.FindName("TagName", row) is TextBox name)
        {
            name.BringIntoView();
            name.Focus();
        }
    }

    private void DeleteTag(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: Draft draft })
            drafts[CategoryTabs.SelectedIndex].Remove(draft);
    }

    private void ChooseColor(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: Draft draft }) return;
        using var picker = new Forms.ColorDialog
        {
            Color = System.Drawing.ColorTranslator.FromHtml(draft.Color),
            FullOpen = true
        };
        if (picker.ShowDialog(new DialogOwner(new WindowInteropHelper(this).Handle)) == Forms.DialogResult.OK)
            draft.SetColor($"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}");
    }

    private void SaveTags(object sender, RoutedEventArgs args)
    {
        try
        {
            var updated = new TagCatalog(drafts.Select(group => group.Select(row => row.Definition).ToArray()).ToArray());
            updated.Save(file, Catalog);
            Catalog = updated;
            DialogResult = true;
        }
        catch (Exception error)
        {
            Notice.Text = error.Message;
        }
    }
}
