using System.Net.Http;
using System.Text.Json;

namespace GlassDesk.Dock;

public partial class DockWindow
{
    /// <summary>
    /// 用 Photon（komoot，基于 OSM）解析地名，支持到区级。
    /// </summary>
    /// <remarks>
    /// 实测依据：Open-Meteo 的地理编码库里**没有**"天河区"，搜"天河"只会得到福建/浙江的
    /// 同名村庄；而 Photon 对"天河区"直接给出 district 级结果
    /// （name=天河区、city=广州市、state=广东省，坐标 113.356/23.127），
    /// 对"广州 天河"这种多词输入也能取到同一条。
    /// <para>
    /// 它免费、无需 API key。注意 <c>lang</c> 只支持 default/de/en/fr，**不能传 zh**，
    /// 但不传时返回的就是本地名称（国内即中文），所以这里不传。
    /// 成功时就地写好经纬度与显示名并返回 true；任何失败都返回 false，由调用方兜底。
    /// </para>
    /// </remarks>
    private async Task<bool> TryLocateWithPhotonAsync(string query)
    {
        try
        {
            using var response = await WeatherHttp.GetAsync(
                "https://photon.komoot.io/api/?q=" + Uri.EscapeDataString(query) + "&limit=5",
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("features", out var features)
                || features.ValueKind != JsonValueKind.Array || features.GetArrayLength() == 0)
            {
                return false;
            }

            // 按输入分词打分挑最贴合的候选：name 命中记 3 分（就是这个地方本身），
            // city/district/county/state 命中记 1 分；行政地名（osm_key=place）另加 2 分，
            // 免得被同名的学校、公交站之类抢走（实测返回里确实混着这些）。
            var tokens = query.Split([' ', ',', '，', '　'], StringSplitOptions.RemoveEmptyEntries);
            var best = default(JsonElement);
            var bestScore = -1;
            var bestLat = 0d;
            var bestLon = 0d;
            foreach (var feature in features.EnumerateArray())
            {
                if (!feature.TryGetProperty("properties", out var properties)
                    || !feature.TryGetProperty("geometry", out var geometry)
                    || !geometry.TryGetProperty("coordinates", out var coordinates)
                    || coordinates.ValueKind != JsonValueKind.Array || coordinates.GetArrayLength() < 2)
                {
                    continue;
                }

                var score = properties.TryGetProperty("osm_key", out var layer)
                    && layer.ValueKind == JsonValueKind.String
                    && string.Equals(layer.GetString(), "place", StringComparison.Ordinal) ? 2 : 0;

                foreach (var token in tokens)
                {
                    if (MatchesField(properties, "name", token))
                    {
                        score += 3;
                    }
                    else if (MatchesField(properties, "city", token) || MatchesField(properties, "district", token)
                        || MatchesField(properties, "county", token) || MatchesField(properties, "state", token))
                    {
                        score += 1;
                    }
                }

                if (score <= bestScore)
                {
                    continue;
                }

                bestScore = score;
                best = properties;
                // GeoJSON 的坐标顺序是 [经度, 纬度]，不能颠倒。
                bestLon = coordinates[0].GetDouble();
                bestLat = coordinates[1].GetDouble();
            }

            if (bestScore < 0)
            {
                return false;
            }

            _weatherLat = bestLat;
            _weatherLon = bestLon;

            // 显示名优先带市一级（用户更熟悉），没有市就退回省。
            var name = ReadString(best, "name");
            var city = ReadString(best, "city");
            var state = ReadString(best, "state");
            var label = string.IsNullOrWhiteSpace(name) ? query : name;
            var qualifier = !string.IsNullOrWhiteSpace(city) ? city : state;
            if (!string.IsNullOrWhiteSpace(qualifier) && !string.Equals(qualifier, label, StringComparison.Ordinal))
            {
                label += $" {qualifier}";
            }

            _weatherLocation = label;
            _weather = new WeatherSnapshot { Ready = false, Offline = false, Location = _weatherLocation, FetchedUtc = DateTime.UtcNow };
            _weatherLocateStarted = true;
            await RefreshWeatherAsync();
            return true;
        }
        catch (Exception)
        {
            // 网络或解析失败都交给下游兜底，不在这里抛。
            return false;
        }
    }

    private static bool MatchesField(JsonElement properties, string field, string token) =>
        properties.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
        && text.Contains(token, StringComparison.Ordinal);

    private static string ReadString(JsonElement properties, string field) =>
        properties.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
