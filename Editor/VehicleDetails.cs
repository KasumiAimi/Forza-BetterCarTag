namespace FH6GarageEditor;

internal static class VehicleDetails
{
    // SimPeakPower is stored in hundreds of watts.
    private const double WattsPerHorsepower = 745.6998715822702;

    internal static int? Horsepower(double peakPower)
        => RoundPositive(peakPower * 100 / WattsPerHorsepower);

    // CurbWeight is stored in hundreds of kilograms.
    internal static int? WeightKg(double curbWeight) => RoundPositive(curbWeight * 100);

    private static int? RoundPositive(double value)
    {
        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return rounded is > 0 and <= int.MaxValue ? (int)rounded : null;
    }

    internal static string? DriveLayout(int type) => type switch
    {
        1 => "前驱（FWD）",
        2 => "后驱（RWD）",
        3 => "四驱（AWD）",
        _ => null
    };

    internal static string TireName(string model, bool stock)
    {
        string name = model.ToUpperInvariant() switch
        {
            "OFFROAD_SNOW_OW" => "越野雪地胎",
            "VINTAGE_WHITEWALL" or "VINTAGE_WHITEWALL_OW" => "复古白边胎",
            "VINTAGE_RACE" or "VINTAGE_RACE_OW" or "VINTAGE_RACE_FIRESTONE" => "复古赛车胎",
            "SEMI_SLICK_HORIZON" or "SEMI_SLICK_HORIZON_OW" => "地平线半热熔胎",
            "SLICK_DRAG" or "SLICK_DRAG_OW" => "直线加速胎",
            _ => model.Split('_', 2)[0].ToUpperInvariant() switch
            {
                "OFFROAD" => "越野胎",
                "RALLY" => "拉力胎",
                "ROAD" => "公路胎",
                "SEMI" => "半热熔胎",
                "SLICK" => "光头胎",
                "SNOW" => "雪地胎",
                "SPORT" => "运动胎",
                "STREET" => "街道胎",
                "VINTAGE" => "复古胎",
                _ => model
            }
        };
        return stock ? name + "（原厂）" : name;
    }
}
