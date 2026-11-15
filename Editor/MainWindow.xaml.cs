using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FH6LocalCryptoTool.GarageViewer;
using Microsoft.Win32;

namespace FH6GarageEditor;

public partial class MainWindow : Window
{
    private sealed record ProfileOption(SaveSource Source, string Display);
    private sealed record CardView(Border Frame, StackPanel Tags, Image Image, TextBlock Placeholder);
    private const int PageSize = 24;
    private const string Untagged = "未标记";
    private const string AllMakes = "全部制造商";
    private static readonly string[] Classes = ["D", "C", "B", "A", "S1", "S2", "R", "X"];
    private static readonly string[] ClassColors = ["#187CA3", "#A66B00", "#A34811", "#E40045", TagColors.S1, "#195BDB", "#8555AF", "#176453"];
    private readonly Dictionary<int, CardView> visible = [];
    private List<Car> cars = [];
    private Car? selected;
    private TagStore? tags;
    private CarPictures? pictures;
    private string? game;
    private bool loading;
    private bool updateControls;
    private int page;
    private int displayRevision;
    private int detailRevision;

    public MainWindow()
    {
        InitializeComponent();
        ClassFilter.ItemsSource = new[] { "全部等级" }.Concat(Classes).ToArray();
        ClassFilter.SelectedIndex = 0;
        RaceTag.ItemsSource = new[] { Untagged }.Concat(TagColors.Uses.Keys).ToArray();
        StyleTag.ItemsSource = new[] { Untagged }.Concat(TagColors.Styles.Keys).ToArray();
        StrengthTag.ItemsSource = new[] { Untagged }.Concat(TagColors.Strengths.Keys).ToArray();
        Loaded += async (_, _) => await RefreshProfiles();
        Closed += (_, _) => pictures?.Dispose();
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
            game = GarageReader.FindGame();
            var loaded = await Task.Run(() => GarageReader.Read(source, game));
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
            Status.Text = $"已自动读取 {cars.Count:N0} 台车 · {(source.Backup ? "数据来自本机备份" : $"存档更新于 {source.Modified:MM-dd HH:mm}")}";
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
            RaceTag.IsEnabled = StyleTag.IsEnabled = StrengthTag.IsEnabled = selected is not null;
            ApplyButton.IsEnabled = tags is not null && game is not null;
        }
    }

    private void Render()
    {
        if (Cards is null || Search is null || Makes is null) return;
        int revision = ++displayRevision;
        string query = Search.Text.Trim();
        var make = Makes.SelectedItem as string;
        if (make == AllMakes) make = null;
        int? classId = ClassFilter?.SelectedIndex > 0 ? ClassFilter.SelectedIndex - 1 : null;
        var filtered = FilterCars(cars, make, query, classId, FavoriteOnly?.IsChecked == true)
            .OrderBy(car => car.Make).ThenBy(car => car.Name).ThenByDescending(car => car.Pi).ThenBy(car => car.Id)
            .ToList();
        int pages = Math.Max(1, (filtered.Count + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 0, pages - 1);
        var pageCars = filtered.Skip(page * PageSize).Take(PageSize).ToArray();
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
        var content = new Grid { Background = Brushes.White, ClipToBounds = true };
        foreach (double height in new[] { 25d, 16d, 20d, -1d, 26d })
            content.RowDefinitions.Add(new RowDefinition { Height = height < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(height) });
        Add(content, CardText(car.Name.ToUpperInvariant(), 16, "#111111", true), 0);
        Add(content, CardText($"{(car.Year > 0 ? car.Year + " " : "")}{car.Make.ToUpperInvariant()}", 12, "#535353"), 1);
        var chips = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        Add(content, chips, 2);
        var image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(7, 0, 7, 0) };
        Add(content, image, 3);
        var placeholder = CardText("正在读取图片…", 12, "#707070");
        Add(content, placeholder, 3);
        var bottom = new DockPanel { Background = TagColors.Brush("#222222"), LastChildFill = true };
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
            Background = Brushes.White, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            Template = CardButtonTemplate() };
        button.Click += (_, _) => SelectCar(car);
        System.Windows.Automation.AutomationProperties.SetName(button, $"{car.Make} {car.Name} {Rating(car)} 车库编号 {car.Id}");
        var frame = new Border { Child = button, Background = Brushes.White,
            BorderThickness = new Thickness(3), BorderBrush = Brushes.Transparent, CornerRadius = new CornerRadius(5),
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

    private static void FillBadges(CardView view, TagSet carTags)
    {
        view.Tags.Children.Clear();
        foreach (var (colors, read) in TagColors.Categories)
        {
            if (read(carTags) is not string label) continue;
            var badge = CardText(label, 12, TagColors.Foreground(label), true);
            badge.Background = TagColors.Brush(colors[label]);
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
        foreach (var item in visible)
            item.Value.Frame.BorderBrush = item.Key == car?.Id ? TagColors.Brush("#C9FF00") : Brushes.Transparent;
        int version = ++detailRevision;
        updateControls = true;
        DetailsName.Text = car?.Name ?? "选择一台车";
        DetailsMeta.Text = car is null ? "" : $"{car.Year} {car.Make}\n{Rating(car)} · 车库编号 #{car.Id}";
        DetailsImage.Source = null;
        ImageSource.Text = car is null ? "" : "正在读取图片…";
        RaceTag.IsEnabled = StyleTag.IsEnabled = StrengthTag.IsEnabled = car is not null && !loading;
        var carTags = car is null ? TagSet.Empty : tags?.Get(car) ?? TagSet.Empty;
        RaceTag.SelectedItem = carTags.Use ?? Untagged;
        StyleTag.SelectedItem = carTags.Style ?? Untagged;
        StrengthTag.SelectedItem = carTags.Strength ?? Untagged;
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
    private void SearchChanged(object sender, TextChangedEventArgs args) { if (!loading) { page = 0; Render(); } }
    private void PreviousClick(object sender, RoutedEventArgs args) { page--; Render(); }
    private void NextClick(object sender, RoutedEventArgs args) { page++; Render(); }
    private void TagsChanged(object sender, SelectionChangedEventArgs args)
    {
        if (loading || updateControls || selected is not Car car || tags is null ||
            RaceTag.SelectedItem is not string use || StyleTag.SelectedItem is not string style ||
            StrengthTag.SelectedItem is not string strength) return;
        try
        {
            var carTags = new TagSet(use == Untagged ? null : use, style == Untagged ? null : style,
                strength == Untagged ? null : strength);
            tags.Save(car, carTags);
            if (visible.TryGetValue(car.Id, out var view)) FillBadges(view, carTags);
            Status.Text = $"已保存 #{car.Id} 的标签。退出游戏后可应用到原生车卡。";
        }
        catch (Exception error) { SelectCar(car); Error(error); }
    }
    private async void ApplyClick(object sender, RoutedEventArgs args)
    {
        if (game is null || tags is null || loading) return;
        try
        {
            ApplyButton.IsEnabled = false;
            var records = cars.Select(car => (car, tags.Get(car))).ToArray();
            var location = game;
            await Task.Run(() => BadgePatch.Apply(location, records));
            Status.Text = "已应用。下次启动 FH6 就能看到已标记车辆的徽标。";
        }
        catch (Exception error) { Error(error); }
        finally { ApplyButton.IsEnabled = game is not null && tags is not null; }
    }
    private void Error(Exception error) => Status.Text = error.Message;
}
