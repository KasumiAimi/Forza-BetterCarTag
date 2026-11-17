using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FH6LocalCryptoTool.GarageViewer;
using Microsoft.Win32;

namespace FH6GarageEditor;

public partial class MainWindow : Window
{
    private sealed record ProfileOption(SaveSource Source, string Display);
    private sealed record CardView(Border Frame, StackPanel Tags, Image Image, TextBlock Placeholder);
    private sealed record TagFilterOption(string? Id, string Name);
    private const double CardColumnWidth = 218;
    private const double CardRowHeight = 168;
    private static readonly TagDefinition Untagged = new("", TagCatalog.Untagged, "#000000");
    private static readonly string CatalogFile = Path.Combine(GarageReader.Data, "tag-catalog.json");
    private const string AllMakes = "全部制造商";
    private static readonly string[] Classes = ["D", "C", "B", "A", "S1", "S2", "R", "X"];
    private static readonly string[] ClassColors = ["#187CA3", "#A66B00", "#A34811", "#E40045", TagColors.S1, "#195BDB", "#8555AF", "#176453"];
    private readonly Dictionary<int, CardView> visible = [];
    private List<Car> cars = [];
    private Car? selected;
    private TagStore? tags;
    private TagCatalog catalog = TagCatalog.Default;
    private string? catalogError;
    private CarPictures? pictures;
    private string? game;
    private bool loading;
    private bool applying;
    private bool updateControls;
    private int page;
    private int pageSize = 1;
    private int displayRevision;
    private int detailRevision;

    public MainWindow()
    {
        InitializeComponent();
        ClassFilter.ItemsSource = new[] { "全部等级" }.Concat(Classes).ToArray();
        ClassFilter.SelectedIndex = 0;
        try { catalog = TagCatalog.Load(CatalogFile); }
        catch (Exception error) { catalogError = $"无法读取标签配置：{error.Message}"; }
        RefreshTagOptions();
        AppVersion.Text = $"v{ReleaseUpdates.CurrentVersion}";
        UpdatePane.CanStartInstallation = () => !loading && !applying;
        UpdatePane.UpdateAvailableChanged += available =>
            UpdateIndicator.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        UpdatePane.InstallStateChanged += installing => GarageNav.IsEnabled = !installing;
        Loaded += async (_, _) => await RefreshProfiles();
        Loaded += async (_, _) => await UpdatePane.CheckAsync();
        Closed += (_, _) =>
        {
            UpdatePane.CancelPending();
            pictures?.Dispose();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UseDarkCaption(this);
    }

    internal static void UseDarkCaption(Window window)
    {
        int enabled = 1;
        if (DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 19, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private void ShowGarage(object sender, RoutedEventArgs args)
    {
        if (GaragePage is null || UpdatePane is null) return;
        UpdatePane.Visibility = Visibility.Collapsed;
        GaragePage.Visibility = Visibility.Visible;
    }

    private void ShowUpdater(object sender, RoutedEventArgs args)
    {
        GaragePage.Visibility = Visibility.Collapsed;
        UpdatePane.Visibility = Visibility.Visible;
    }

    private async Task RefreshProfiles()
    {
        if (loading) return;
        try
        {
            loading = true;
            Status.Text = "正在查找本机车库…";
            var discovered = await Task.Run(GarageReader.FindSaves);
            var prior = (Profiles.SelectedItem as ProfileOption)?.Source.ProfileKey;
            var options = discovered.Select((source, index) => new ProfileOption(source,
                $"{(source.Backup ? "本机备份" : "本机车库")} {index + 1} · {source.Modified:MM-dd HH:mm}")).ToList();
            if (options.Count == 0) { Status.Text = "未找到本机车库存档。可以使用「选择存档」导入整个车库。"; return; }
            Profiles.ItemsSource = options;
            var current = options.FirstOrDefault(option => option.Source.ProfileKey == prior) ?? options[0];
            Profiles.SelectedItem = current;
            loading = false;
            await LoadProfile(current.Source);
        }
        catch (Exception error) { Error(error); }
        finally { loading = false; }
    }

    private async Task LoadProfile(SaveSource source)
    {
        if (loading) return;
        try
        {
            loading = true;
            Profiles.IsEnabled = false;
            ApplyButton.IsEnabled = false;
            RaceTag.IsEnabled = StyleTag.IsEnabled = StrengthTag.IsEnabled = false;
            Status.Text = "正在读取车库存档和车型目录…";
            var (location, loaded) = await Task.Run(() =>
            {
                var directory = GarageReader.FindGame();
                return (directory, GarageReader.Read(source, directory));
            });
            game = location;
            GameDirectoryButton.ToolTip = game ?? "选择 FH6 游戏安装目录";
            var newTags = new TagStore(Path.Combine(GarageReader.Data, "tags"), source.ProfileKey);
            pictures?.Dispose();
            pictures = new CarPictures(game);
            cars = loaded;
            tags = newTags;
            selected = null;
            page = 0;
            updateControls = true;
            Makes.ItemsSource = new[] { AllMakes }.Concat(cars.Select(car => car.Make).Distinct().Order()).ToArray();
            Makes.SelectedIndex = 0;
            updateControls = false;
            Render();
            Status.Text = catalogError ?? $"已自动读取 {cars.Count:N0} 台车 · {(source.Backup ? "数据来自本机备份" : $"存档更新于 {source.Modified:MM-dd HH:mm}")}";
            if (catalogError is null && game is null) Status.Text += " · 可选择游戏目录加载完整车辆信息";
        }
        catch (Exception error)
        {
            cars = [];
            tags = null;
            selected = null;
            Render();
            Error(error);
        }
        finally
        {
            loading = false;
            Profiles.IsEnabled = true;
            RaceTag.IsEnabled = StyleTag.IsEnabled = StrengthTag.IsEnabled = selected is not null && catalogError is null;
            ApplyButton.IsEnabled = tags is not null && game is not null && catalogError is null && !applying;
        }
    }

    private void CarViewportChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        int columns = Math.Max(1, (int)(e.NewSize.Width / CardColumnWidth));
        int rows = Math.Max(1, (int)(e.NewSize.Height / CardRowHeight));
        int capacity = columns * rows;
        if (Cards.Columns == columns && pageSize == capacity) return;

        int anchorIndex = page * pageSize;
        Cards.Columns = columns;
        pageSize = capacity;
        page = anchorIndex / pageSize;
        Render(keepSelectionVisible: true);
    }

    private void Render(bool keepSelectionVisible = false)
    {
        if (Cards is null || Search is null || Makes is null) return;
        int revision = ++displayRevision;
        string query = Search.Text.Trim();
        var make = Makes.SelectedItem as string;
        if (make == AllMakes) make = null;
        int? classId = ClassFilter?.SelectedIndex > 0 ? ClassFilter.SelectedIndex - 1 : null;
        var tagFilters = CurrentTagFilters();
        var filtered = FilterCars(cars, make, query, classId, FavoriteOnly?.IsChecked == true)
            .Where(car => tagFilters == TagSet.Empty || catalog.Matches(tags?.Get(car) ?? TagSet.Empty, tagFilters))
            .OrderBy(car => car.Make).ThenBy(car => car.Name).ThenByDescending(car => car.Pi).ThenBy(car => car.Id)
            .ToList();
        if (keepSelectionVisible && selected is not null)
        {
            int selectedIndex = filtered.IndexOf(selected);
            if (selectedIndex >= 0) page = selectedIndex / pageSize;
        }
        int pages = Math.Max(1, (filtered.Count + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 0, pages - 1);
        var pageCars = filtered.Skip(page * pageSize).Take(pageSize).ToArray();
        int columns = Math.Max(1, Cards.Columns);
        Cards.Height = ((pageCars.Length + columns - 1) / columns) * CardRowHeight;
        visible.Clear();
        Cards.Children.Clear();
        foreach (var car in pageCars)
        {
            var view = CreateCard(car);
            visible[car.Id] = view;
            Cards.Children.Add(view.Frame);
            _ = LoadCardImage(car, view, revision);
        }
        Count.Text = $"{filtered.Count:N0} / {cars.Count:N0} 台";
        PageLabel.Text = $"{page + 1} / {pages}";
        Previous.IsEnabled = page > 0;
        Next.IsEnabled = page + 1 < pages;
        CarScroll.ScrollToTop();
        SelectCar(pageCars.FirstOrDefault(car => car == selected) ?? pageCars.FirstOrDefault());
    }

    internal static IEnumerable<Car> FilterCars(IEnumerable<Car> cars, string? make, string query, int? classId, bool favoriteOnly)
        => cars.Where(car => (!favoriteOnly || car.Favorite) &&
            (classId is null || car.ClassId == classId) &&
            (make is null || car.Make == make) &&
            (query.Length == 0 || $"{car.Make} {car.Name} {car.Year} #{car.Id}".Contains(query, StringComparison.OrdinalIgnoreCase)));

    private CardView CreateCard(Car car)
    {
        var content = new Grid { Background = TagColors.Brush("#22272E"), ClipToBounds = true };
        foreach (double height in new[] { 25d, 16d, 20d, -1d, 26d })
            content.RowDefinitions.Add(new RowDefinition { Height = height < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(height) });
        Add(content, CardText(car.Name.ToUpperInvariant(), 16, "#EEF0F4", true), 0);
        Add(content, CardText($"{(car.Year > 0 ? car.Year + " " : "")}{car.Make.ToUpperInvariant()}", 12, "#B8C0CB"), 1);
        var chips = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        Add(content, new Viewbox { Child = chips, StretchDirection = StretchDirection.DownOnly }, 2);
        var image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(7, 0, 7, 0) };
        Add(content, new Border { Background = TagColors.Brush("#EEF0F4"), Child = image }, 3);
        var placeholder = CardText("正在读取图片…", 12, "#48515E");
        Add(content, placeholder, 3);
        var bottom = new DockPanel { Background = TagColors.Brush("#252B33"), LastChildFill = true };
        var pi = CardText(Rating(car), 14, "#FFFFFF", true);
        pi.Background = TagColors.Brush(car.ClassId >= 0 && car.ClassId < ClassColors.Length ? ClassColors[car.ClassId] : "#333333");
        pi.Padding = new Thickness(8, 0, 8, 0);
        DockPanel.SetDock(pi, Dock.Right);
        bottom.Children.Add(pi);
        var status = CardText((car.Favorite ? "♥   " : "") + $"#{car.Id}", 12, "#FFFFFF");
        status.TextAlignment = TextAlignment.Left;
        status.Padding = new Thickness(8, 0, 0, 0);
        bottom.Children.Add(status);
        Add(content, bottom, 4);
        var button = new Button { Content = content, Padding = new Thickness(0), Margin = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            Template = CardButtonTemplate() };
        button.Click += (_, _) => SelectCar(car);
        System.Windows.Automation.AutomationProperties.SetName(button, $"{car.Make} {car.Name} {Rating(car)} 车库编号 {car.Id}");
        var frame = new Border { Child = button, Background = TagColors.Brush("#22272E"),
            BorderThickness = new Thickness(2), BorderBrush = (Brush)FindResource("Stroke"), CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 0, 8, 8), ClipToBounds = true };
        var view = new CardView(frame, chips, image, placeholder);
        FillBadges(view, tags?.Get(car) ?? TagSet.Empty);
        return view;
    }

    private static ControlTemplate CardButtonTemplate()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        return new ControlTemplate(typeof(Button)) { VisualTree = presenter };
    }

    private static void Add(Grid grid, UIElement item, int row)
    {
        Grid.SetRow(item, row);
        grid.Children.Add(item);
    }

    private static TextBlock CardText(string text, double size, string color, bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = TagColors.Brush(color),
        FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static string Rating(Car car) => car.ClassId >= 0 && car.ClassId < Classes.Length
        ? car.Pi > 0 ? $"{Classes[car.ClassId]} {car.Pi}" : Classes[car.ClassId]
        : car.Pi > 0 ? $"PI {car.Pi}" : "—";

    private void FillBadges(CardView view, TagSet carTags)
    {
        view.Tags.Children.Clear();
        for (int category = 0; category < TagCatalog.Categories.Length; category++)
        {
            var tag = catalog.Find(category, TagCatalog.Categories[category].Read(carTags));
            if (tag is null) continue;
            var badge = CardText(tag.Name, 12, tag.Foreground, true);
            badge.Background = TagColors.Brush(tag.Color);
            badge.Padding = new Thickness(6, 0, 6, 0);
            badge.Margin = new Thickness(0, 0, 4, 0);
            view.Tags.Children.Add(badge);
        }
    }

    private async Task LoadCardImage(Car car, CardView view, int revision)
    {
        try
        {
            var result = pictures is null ? (null, "暂无车辆图片") : await pictures.Get(car);
            if (revision != displayRevision) return;
            view.Image.Source = result.Item1;
            view.Placeholder.Text = result.Item2;
            view.Placeholder.Visibility = result.Item1 is null ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            if (revision == displayRevision) view.Placeholder.Text = "暂无车辆图片";
        }
    }

    private void SelectCar(Car? car)
    {
        selected = car;
        var activeBorder = (Brush)FindResource("Lavender");
        var inactiveBorder = (Brush)FindResource("Stroke");
        foreach (var item in visible)
            item.Value.Frame.BorderBrush = item.Key == car?.Id ? activeBorder : inactiveBorder;
        int version = ++detailRevision;
        updateControls = true;
        DetailsName.Text = car?.Name ?? "选择一台车";
        DetailsMeta.Text = car is null ? "" : $"{car.Year} {car.Make}\n{Rating(car)} · 车库编号 #{car.Id}";
        DetailsSpecs.Visibility = car is null ? Visibility.Collapsed : Visibility.Visible;
        string power = car?.Horsepower is int hp ? $"{hp:N0} hp" : "暂无数据";
        string weight = car?.WeightKg is int kg ? $"{kg:N0} kg" : "暂无数据";
        DetailsSpecs.Text = car is null ? "" :
            $"轮胎  {car.Tire ?? "暂无数据"}\n马力  {power}\n车重  {weight}\n驱动形式  {car.DriveLayout ?? "暂无数据"}";
        DetailsImage.Source = null;
        ImageSource.Text = car is null ? "" : "正在读取图片…";
        RaceTag.IsEnabled = StyleTag.IsEnabled = StrengthTag.IsEnabled = car is not null && !loading && catalogError is null;
        var carTags = car is null ? TagSet.Empty : tags?.Get(car) ?? TagSet.Empty;
        RaceTag.SelectedValue = catalog.Find(0, carTags.Use)?.Id ?? Untagged.Id;
        StyleTag.SelectedValue = catalog.Find(1, carTags.Style)?.Id ?? Untagged.Id;
        StrengthTag.SelectedValue = catalog.Find(2, carTags.Strength)?.Id ?? Untagged.Id;
        updateControls = false;
        if (car is not null) _ = LoadDetails(car, version);
    }

    private async Task LoadDetails(Car car, int revision)
    {
        try
        {
            var result = pictures is null ? (null, "暂无车辆图片") : await pictures.Get(car);
            if (revision != detailRevision) return;
            DetailsImage.Source = result.Item1;
            ImageSource.Text = result.Item2;
        }
        catch
        {
            if (revision == detailRevision) ImageSource.Text = "暂无车辆图片";
        }
    }

    private async void ProfileChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!loading && Profiles.SelectedItem is ProfileOption option) await LoadProfile(option.Source);
    }
    private async void RefreshClick(object sender, RoutedEventArgs args) => await RefreshProfiles();
    private async void ChooseGameDirectoryClick(object sender, RoutedEventArgs args)
    {
        if (loading || applying) return;
        var picker = new OpenFolderDialog { Title = "选择包含 forzahorizon6.exe 的游戏文件夹" };
        if (game is not null) picker.InitialDirectory = game;
        if (picker.ShowDialog(this) != true) return;
        try
        {
            GarageReader.SetGameDirectory(picker.FolderName);
            if (Profiles.SelectedItem is ProfileOption option) await LoadProfile(option.Source);
            else
            {
                GameDirectoryButton.ToolTip = GarageReader.ResolveGameDirectory(picker.FolderName);
                Status.Text = "已保存游戏目录。请刷新车库或选择存档。";
            }
        }
        catch (Exception error) { Error(error); }
    }

    private async void OpenSaveClick(object sender, RoutedEventArgs args)
    {
        if (loading) return;
        var picker = new OpenFileDialog { Title = "选择 FH6 车库存档", Filter = "FH6 存档|C_ProfileData;C_ProfileBackup;*ProfileBackup|所有文件|*.*" };
        if (picker.ShowDialog(this) == true)
        {
            var source = GarageReader.Source(picker.FileName, Path.GetDirectoryName(picker.FileName)!, Path.GetFileName(picker.FileName) != "C_ProfileData");
            loading = true;
            var option = new ProfileOption(source, "手动选择的车库存档");
            Profiles.ItemsSource = new[] { option };
            Profiles.SelectedItem = option;
            loading = false;
            await LoadProfile(source);
        }
    }
    private void FilterChanged(object sender, RoutedEventArgs args) { if (!updateControls && !loading) { page = 0; Render(); } }
    private void ClearFiltersClick(object sender, RoutedEventArgs args)
    {
        updateControls = true;
        Makes.SelectedIndex = Makes.HasItems ? 0 : -1;
        ClassFilter.SelectedIndex = 0;
        RaceFilter.SelectedIndex = 0;
        StyleFilter.SelectedIndex = 0;
        StrengthFilter.SelectedIndex = 0;
        FavoriteOnly.IsChecked = false;
        Search.Clear();
        updateControls = false;
        page = 0;
        Render();
    }

    private void PreviousClick(object sender, RoutedEventArgs args) { page--; Render(); }
    private void NextClick(object sender, RoutedEventArgs args) { page++; Render(); }

    private TagSet CurrentTagFilters() => new(
        (RaceFilter?.SelectedItem as TagFilterOption)?.Id,
        (StyleFilter?.SelectedItem as TagFilterOption)?.Id,
        (StrengthFilter?.SelectedItem as TagFilterOption)?.Id);

    private void RefreshTagOptions()
    {
        updateControls = true;
        RaceTag.ItemsSource = new[] { Untagged }.Concat(catalog[0]).ToArray();
        StyleTag.ItemsSource = new[] { Untagged }.Concat(catalog[1]).ToArray();
        StrengthTag.ItemsSource = new[] { Untagged }.Concat(catalog[2]).ToArray();
        SetTagFilterOptions(RaceFilter, 0);
        SetTagFilterOptions(StyleFilter, 1);
        SetTagFilterOptions(StrengthFilter, 2);
        updateControls = false;
    }

    private void SetTagFilterOptions(ComboBox control, int category)
    {
        var previous = (control.SelectedItem as TagFilterOption)?.Id;
        var all = new TagFilterOption(null, "全部（不限）");
        var options = new[] { all, new TagFilterOption("", TagCatalog.Untagged) }
            .Concat(catalog[category].Select(tag => new TagFilterOption(tag.Id, tag.Name))).ToArray();
        control.ItemsSource = options;
        var same = options.FirstOrDefault(option => option.Id == previous);
        control.SelectedItem = same ?? all;
        if (same is null) page = 0;
    }

    private void ManageTagsClick(object sender, RoutedEventArgs args)
    {
        if (applying) return;
        try
        {
            var current = TagCatalog.Load(CatalogFile);
            var manager = new TagManagerWindow(current, CatalogFile) { Owner = this };
            if (manager.ShowDialog() != true) return;
            catalog = manager.Catalog;
            catalogError = null;
            RefreshTagOptions();
            Render();
            ApplyButton.IsEnabled = !loading && tags is not null && game is not null;
            Status.Text = "已保存标签设置。退出游戏后可重新应用到原生车卡。";
        }
        catch (Exception error) { Error(error); }
    }

    private void TagsChanged(object sender, SelectionChangedEventArgs args)
    {
        if (loading || updateControls || catalogError is not null || selected is not Car car || tags is null ||
            RaceTag.SelectedItem is not TagDefinition use || StyleTag.SelectedItem is not TagDefinition style ||
            StrengthTag.SelectedItem is not TagDefinition strength) return;
        try
        {
            var carTags = new TagSet(use.Id.Length == 0 ? null : use.Id, style.Id.Length == 0 ? null : style.Id,
                strength.Id.Length == 0 ? null : strength.Id);
            tags.Save(car, carTags, catalog);
            if (!catalog.Matches(carTags, CurrentTagFilters())) Render();
            else if (visible.TryGetValue(car.Id, out var view)) FillBadges(view, carTags);
            Status.Text = $"已保存 #{car.Id} 的标签。退出游戏后可应用到原生车卡。";
        }
        catch (Exception error) { SelectCar(car); Error(error); }
    }
    private async void ApplyClick(object sender, RoutedEventArgs args)
    {
        if (game is null || tags is null || loading || applying || catalogError is not null) return;
        try
        {
            applying = true;
            ApplyButton.IsEnabled = ManageTagsButton.IsEnabled = false;
            var records = cars.Select(car => (car, tags.Get(car))).ToArray();
            var location = game;
            var definitions = catalog;
            await Task.Run(() => BadgePatch.Apply(location, records, definitions));
            Status.Text = "已应用。下次启动 FH6 就能看到已标记车辆的徽标。";
        }
        catch (Exception error) { Error(error); }
        finally
        {
            applying = false;
            ManageTagsButton.IsEnabled = true;
            ApplyButton.IsEnabled = !loading && game is not null && tags is not null && catalogError is null;
        }
    }
    private void Error(Exception error) => Status.Text = error.Message;
}
