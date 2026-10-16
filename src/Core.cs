using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace ShpToKmz
{
    // ---------------------------------------------------------------- 几何
    public class Part
    {
        public double[] X;
        public double[] Y;
        public Part(int n) { X = new double[n]; Y = new double[n]; }
        public int Count { get { return X.Length; } }
    }

    public enum GeomKind { Null, Point, MultiPoint, Line, Polygon }

    public class Shape
    {
        public GeomKind Kind;
        public List<Part> Parts = new List<Part>();
    }

    public class ShpFile
    {
        public int ShapeType;
        public double MinX, MinY, MaxX, MaxY;
        public List<Shape> Shapes = new List<Shape>();
        public int Unsupported;
    }

    public static class ShpReader
    {
        static int ReadBE(BinaryReader r)
        {
            byte[] b = r.ReadBytes(4);
            if (b.Length < 4) throw new EndOfStreamException();
            return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        }

        public static ShpFile Read(string path)
        {
            var shp = new ShpFile();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
            using (var r = new BinaryReader(fs))
            {
                if (fs.Length < 100) throw new InvalidDataException("SHP 文件头不完整");
                if (ReadBE(r) != 9994) throw new InvalidDataException("不是有效的 SHP 文件");
                fs.Position = 32;
                shp.ShapeType = r.ReadInt32();
                shp.MinX = r.ReadDouble(); shp.MinY = r.ReadDouble();
                shp.MaxX = r.ReadDouble(); shp.MaxY = r.ReadDouble();
                fs.Position = 100;
                while (fs.Length - fs.Position >= 8)
                {
                    ReadBE(r);
                    int len = ReadBE(r) * 2;
                    if (len < 4 || fs.Position + len > fs.Length) break;
                    byte[] buf = r.ReadBytes(len);
                    Shape s = Parse(buf);
                    if (s == null) { shp.Unsupported++; s = new Shape { Kind = GeomKind.Null }; }
                    shp.Shapes.Add(s);
                }
            }
            return shp;
        }

        static double D(byte[] b, int o) { return BitConverter.ToDouble(b, o); }

        static Shape Parse(byte[] b)
        {
            int t = BitConverter.ToInt32(b, 0);
            var s = new Shape();
            switch (t)
            {
                case 0:
                    s.Kind = GeomKind.Null;
                    return s;
                case 1: case 11: case 21:
                {
                    s.Kind = GeomKind.Point;
                    var p = new Part(1);
                    p.X[0] = D(b, 4); p.Y[0] = D(b, 12);
                    s.Parts.Add(p);
                    return s;
                }
                case 8: case 18: case 28:
                {
                    s.Kind = GeomKind.MultiPoint;
                    int n = BitConverter.ToInt32(b, 36);
                    for (int i = 0; i < n; i++)
                    {
                        var p = new Part(1);
                        p.X[0] = D(b, 40 + 16 * i); p.Y[0] = D(b, 48 + 16 * i);
                        s.Parts.Add(p);
                    }
                    return s;
                }
                case 3: case 13: case 23:
                case 5: case 15: case 25:
                {
                    s.Kind = (t % 10 == 3) ? GeomKind.Line : GeomKind.Polygon;
                    int np = BitConverter.ToInt32(b, 36);
                    int npt = BitConverter.ToInt32(b, 40);
                    int ptBase = 44 + 4 * np;
                    for (int i = 0; i < np; i++)
                    {
                        int start = BitConverter.ToInt32(b, 44 + 4 * i);
                        int end = i + 1 < np ? BitConverter.ToInt32(b, 44 + 4 * (i + 1)) : npt;
                        if (start < 0 || end > npt || end <= start) continue;
                        var p = new Part(end - start);
                        for (int k = start; k < end; k++)
                        {
                            p.X[k - start] = D(b, ptBase + 16 * k);
                            p.Y[k - start] = D(b, ptBase + 16 * k + 8);
                        }
                        s.Parts.Add(p);
                    }
                    return s;
                }
                default:
                    return null; // MultiPatch 等
            }
        }
    }

    // ---------------------------------------------------------------- DBF
    public class DbfField
    {
        public string Name;
        public char Type;
        public int Length;
        public int Offset;
    }

    public class DbfTable
    {
        public List<DbfField> Fields = new List<DbfField>();
        public List<string[]> Records = new List<string[]>(); // 已删除记录为 null
        public Encoding Encoding;
        public string EncodingInfo;

        public int FieldIndex(string name)
        {
            for (int i = 0; i < Fields.Count; i++)
                if (string.Equals(Fields[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        // encodingMode: "auto" / "gbk" / "utf8"
        public static DbfTable Read(string path, string encodingMode, bool headerOnly)
        {
            byte[] data;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (headerOnly)
                {
                    int n = (int)Math.Min(fs.Length, 65536);
                    data = new byte[n];
                    int read = 0;
                    while (read < n) { int k = fs.Read(data, read, n - read); if (k <= 0) break; read += k; }
                }
                else
                {
                    data = new byte[fs.Length];
                    int read = 0;
                    while (read < data.Length) { int k = fs.Read(data, read, data.Length - read); if (k <= 0) break; read += k; }
                }
            }
            if (data.Length < 32) throw new InvalidDataException("DBF 文件头不完整");

            var t = new DbfTable();
            int numRecords = BitConverter.ToInt32(data, 4);
            int headerLen = BitConverter.ToUInt16(data, 8);
            int recordLen = BitConverter.ToUInt16(data, 10);
            byte ldid = data[29];

            string info;
            t.Encoding = ChooseEncoding(path, ldid, data, headerLen, encodingMode, out info);
            t.EncodingInfo = info;

            int pos = 32, offset = 1;
            while (pos + 32 <= data.Length && data[pos] != 0x0D)
            {
                int nameLen = 0;
                while (nameLen < 11 && data[pos + nameLen] != 0) nameLen++;
                var f = new DbfField
                {
                    Name = t.Encoding.GetString(data, pos, nameLen).Trim(),
                    Type = (char)data[pos + 11],
                    Length = data[pos + 16],
                    Offset = offset
                };
                offset += f.Length;
                t.Fields.Add(f);
                pos += 32;
            }
            if (headerOnly) return t;

            for (int r = 0; r < numRecords; r++)
            {
                int start = headerLen + r * recordLen;
                if (start + recordLen > data.Length) break;
                if (data[start] == (byte)'*') { t.Records.Add(null); continue; }
                var vals = new string[t.Fields.Count];
                for (int i = 0; i < t.Fields.Count; i++)
                {
                    DbfField f = t.Fields[i];
                    string raw = t.Encoding.GetString(data, start + f.Offset, f.Length).Replace("\0", "").Trim();
                    vals[i] = FormatValue(f.Type, raw);
                }
                t.Records.Add(vals);
            }
            return t;
        }

        static string FormatValue(char type, string raw)
        {
            if (raw.Length == 0) return raw;
            if (type == 'N' || type == 'F')
            {
                // 去掉多余的 0，并把 1.00000000000e+001 这类科学计数法还原成 10
                double d;
                if (raw.IndexOfAny(new[] { 'e', 'E' }) >= 0 &&
                    double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    return d.ToString("0.###############", CultureInfo.InvariantCulture);
                if (raw.IndexOf('.') >= 0)
                {
                    raw = raw.TrimEnd('0');
                    if (raw.EndsWith(".")) raw = raw.Substring(0, raw.Length - 1);
                    if (raw.Length == 0 || raw == "-") raw = "0";
                }
            }
            else if (type == 'D' && raw.Length == 8)
            {
                raw = raw.Substring(0, 4) + "-" + raw.Substring(4, 2) + "-" + raw.Substring(6, 2);
            }
            return raw;
        }

        static Encoding ChooseEncoding(string dbfPath, byte ldid, byte[] data, int headerLen, string mode, out string info)
        {
            if (mode == "gbk") { info = "GBK（手动指定）"; return Encoding.GetEncoding(936); }
            if (mode == "utf8") { info = "UTF-8（手动指定）"; return new UTF8Encoding(false); }

            string cpg = Path.ChangeExtension(dbfPath, ".cpg");
            if (File.Exists(cpg))
            {
                string s = File.ReadAllText(cpg).Trim();
                Encoding e = FromCpg(s);
                if (e != null) { info = e.WebName.ToUpperInvariant() + "（来自 .cpg）"; return e; }
            }
            switch (ldid)
            {
                case 0x4D: case 0x7A: info = "GBK（来自 DBF 语言标记）"; return Encoding.GetEncoding(936);
                case 0x4F: case 0x78: info = "Big5（来自 DBF 语言标记）"; return Encoding.GetEncoding(950);
            }
            // 没有标记：检查记录区是否是合法 UTF-8
            bool hasHigh = false;
            for (int i = 0; i < data.Length; i++) if (data[i] >= 0x80) { hasHigh = true; break; }
            if (hasHigh)
            {
                try
                {
                    new UTF8Encoding(false, true).GetString(data, 32, Math.Min(data.Length, 4 << 20) - 32);
                    info = "UTF-8（自动检测）";
                    return new UTF8Encoding(false);
                }
                catch (DecoderFallbackException) { }
            }
            info = "GBK（默认）";
            return Encoding.GetEncoding(936);
        }

        static Encoding FromCpg(string s)
        {
            string u = s.ToUpperInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
            if (u == "UTF8" || u == "65001") return new UTF8Encoding(false);
            if (u == "GBK" || u == "936" || u == "CP936" || u == "GB2312" || u == "ANSI936") return Encoding.GetEncoding(936);
            if (u == "GB18030" || u == "54936") return Encoding.GetEncoding(54936);
            if (u == "BIG5" || u == "950" || u == "CP950") return Encoding.GetEncoding(950);
            if (u == "SYSTEM" || u == "ANSI" || u == "0") return Encoding.GetEncoding(936);
            if (u.StartsWith("ANSI")) u = u.Substring(4);
            if (u.StartsWith("CP")) u = u.Substring(2);
            int cp;
            if (int.TryParse(u, out cp)) { try { return Encoding.GetEncoding(cp); } catch { } }
            try { return Encoding.GetEncoding(s.Trim()); } catch { return null; }
        }
    }

    // ---------------------------------------------------------------- 坐标转换
    public interface ICoordTransform
    {
        void ToLonLat(double x, double y, out double lon, out double lat);
        string Description { get; }
    }

    public class Geographic : ICoordTransform
    {
        public void ToLonLat(double x, double y, out double lon, out double lat) { lon = x; lat = y; }
        public string Description { get { return "地理坐标（经纬度）"; } }
    }

    // 横轴墨卡托 / 高斯-克吕格反算（Snyder, USGS PP1395）
    public class TransverseMercator : ICoordTransform
    {
        readonly double a, e2, ep2, k0, lon0, fe, fn, unit, m0, e1, mu1;
        readonly string desc;

        public TransverseMercator(double a, double invF, double lon0Deg, double lat0Deg, double k0,
                                  double falseEasting, double falseNorthing, double unitToMeter, string desc)
        {
            this.a = a;
            double f = invF == 0 ? 0 : 1 / invF;
            e2 = f * (2 - f);
            ep2 = e2 / (1 - e2);
            this.k0 = k0;
            lon0 = lon0Deg * Math.PI / 180;
            fe = falseEasting; fn = falseNorthing; unit = unitToMeter;
            m0 = Meridian(lat0Deg * Math.PI / 180);
            double s = Math.Sqrt(1 - e2);
            e1 = (1 - s) / (1 + s);
            mu1 = a * (1 - e2 / 4 - 3 * e2 * e2 / 64 - 5 * e2 * e2 * e2 / 256);
            this.desc = desc;
        }

        double Meridian(double phi)
        {
            double e4 = e2 * e2, e6 = e4 * e2;
            return a * ((1 - e2 / 4 - 3 * e4 / 64 - 5 * e6 / 256) * phi
                      - (3 * e2 / 8 + 3 * e4 / 32 + 45 * e6 / 1024) * Math.Sin(2 * phi)
                      + (15 * e4 / 256 + 45 * e6 / 1024) * Math.Sin(4 * phi)
                      - (35 * e6 / 3072) * Math.Sin(6 * phi));
        }

        public void ToLonLat(double x, double y, out double lon, out double lat)
        {
            double X = (x - fe) * unit, Y = (y - fn) * unit;
            double M = m0 + Y / k0;
            double mu = M / mu1;
            double phi1 = mu + (3 * e1 / 2 - 27 * Math.Pow(e1, 3) / 32) * Math.Sin(2 * mu)
                             + (21 * e1 * e1 / 16 - 55 * Math.Pow(e1, 4) / 32) * Math.Sin(4 * mu)
                             + (151 * Math.Pow(e1, 3) / 96) * Math.Sin(6 * mu)
                             + (1097 * Math.Pow(e1, 4) / 512) * Math.Sin(8 * mu);
            double sin1 = Math.Sin(phi1), cos1 = Math.Cos(phi1), tan1 = Math.Tan(phi1);
            double C1 = ep2 * cos1 * cos1;
            double T1 = tan1 * tan1;
            double w = 1 - e2 * sin1 * sin1;
            double N1 = a / Math.Sqrt(w);
            double R1 = a * (1 - e2) / (w * Math.Sqrt(w));
            double D = X / (N1 * k0);
            double D2 = D * D;
            double latR = phi1 - (N1 * tan1 / R1) * (D2 / 2
                          - (5 + 3 * T1 + 10 * C1 - 4 * C1 * C1 - 9 * ep2) * D2 * D2 / 24
                          + (61 + 90 * T1 + 298 * C1 + 45 * T1 * T1 - 252 * ep2 - 3 * C1 * C1) * D2 * D2 * D2 / 720);
            double lonR = lon0 + (D - (1 + 2 * T1 + C1) * D2 * D / 6
                          + (5 - 2 * C1 + 28 * T1 - 3 * C1 * C1 + 8 * ep2 + 24 * T1 * T1) * D2 * D2 * D / 120) / cos1;
            lat = latR * 180 / Math.PI;
            lon = lonR * 180 / Math.PI;
        }

        public string Description { get { return desc; } }
    }

    // 墨卡托反算（含 Web 墨卡托）
    public class Mercator : ICoordTransform
    {
        readonly double a, e, k0, lon0, fe, fn, unit;
        readonly string desc;

        public Mercator(double a, double invF, double lon0Deg, double k0, double fe, double fn, double unit, string desc)
        {
            this.a = a;
            double f = invF == 0 ? 0 : 1 / invF;
            e = Math.Sqrt(f * (2 - f));
            this.k0 = k0; lon0 = lon0Deg * Math.PI / 180; this.fe = fe; this.fn = fn; this.unit = unit;
            this.desc = desc;
        }

        public void ToLonLat(double x, double y, out double lon, out double lat)
        {
            double X = (x - fe) * unit, Y = (y - fn) * unit;
            lon = (X / (a * k0) + lon0) * 180 / Math.PI;
            double t = Math.Exp(-Y / (a * k0));
            double phi = Math.PI / 2 - 2 * Math.Atan(t);
            for (int i = 0; i < 15 && e > 0; i++)
            {
                double es = e * Math.Sin(phi);
                double next = Math.PI / 2 - 2 * Math.Atan(t * Math.Pow((1 - es) / (1 + es), e / 2));
                if (Math.Abs(next - phi) < 1e-12) { phi = next; break; }
                phi = next;
            }
            lat = phi * 180 / Math.PI;
        }

        public string Description { get { return desc; } }
    }

    public static class Crs
    {
        const double CgcsA = 6378137.0, CgcsInvF = 298.257222101;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static ICoordTransform FromPrj(string wkt, List<string> warnings)
        {
            CheckDatum(wkt, warnings);
            if (wkt.IndexOf("PROJCS", StringComparison.OrdinalIgnoreCase) < 0 &&
                wkt.IndexOf("PROJCRS", StringComparison.OrdinalIgnoreCase) < 0)
            {
                if (wkt.IndexOf("GEOGCS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    wkt.IndexOf("GEOGCRS", StringComparison.OrdinalIgnoreCase) >= 0)
                    return new Geographic();
                throw new NotSupportedException("无法解析 .prj 文件内容");
            }

            string pcsName = Match(wkt, @"PROJCS\[\s*""([^""]*)""");
            Match pm = Regex.Match(wkt, @"PROJECTION\[\s*""([^""]+)""", RegexOptions.IgnoreCase);
            if (!pm.Success) throw new NotSupportedException("无法解析 .prj 中的投影方式");
            string proj = pm.Groups[1].Value.ToLowerInvariant().Replace(" ", "_");

            double a = CgcsA, invF = CgcsInvF;
            Match sm = Regex.Match(wkt, @"SPHEROID\[\s*""[^""]*""\s*,\s*([-+0-9.eE]+)\s*,\s*([-+0-9.eE]+)", RegexOptions.IgnoreCase);
            if (sm.Success)
            {
                a = double.Parse(sm.Groups[1].Value, Inv);
                invF = double.Parse(sm.Groups[2].Value, Inv);
            }

            var par = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(wkt, @"PARAMETER\[\s*""([^""]+)""\s*,\s*([-+0-9.eE]+)\s*\]", RegexOptions.IgnoreCase))
            {
                string k = m.Groups[1].Value.Replace(" ", "_");
                if (!par.ContainsKey(k)) par[k] = double.Parse(m.Groups[2].Value, Inv);
            }

            double unit = 1.0;
            Match um = Regex.Match(wkt.Substring(pm.Index), @"UNIT\[\s*""([^""]*)""\s*,\s*([-+0-9.eE]+)", RegexOptions.IgnoreCase);
            if (um.Success) unit = double.Parse(um.Groups[2].Value, Inv);

            double fe = Get(par, 0, "False_Easting");
            double fn = Get(par, 0, "False_Northing");
            double lon0 = Get(par, 0, "Central_Meridian", "Longitude_Of_Center", "Longitude_Of_Origin");
            double lat0 = Get(par, 0, "Latitude_Of_Origin", "Latitude_Of_Center");
            double k0 = Get(par, 1, "Scale_Factor");

            string desc = (pcsName ?? proj) + "（中央经线 " + lon0.ToString(Inv) + "°）";

            if (proj.Contains("gauss") || proj.Contains("transverse_mercator"))
                return new TransverseMercator(a, invF, lon0, lat0, k0, fe, fn, unit, desc);

            if (proj.Contains("mercator_auxiliary_sphere") || proj.Contains("pseudo_mercator") ||
                proj.Contains("popular_visualisation"))
                return new Mercator(a, 0, lon0, 1, fe, fn, unit, pcsName ?? "Web 墨卡托");

            if (proj == "mercator" || proj.StartsWith("mercator_1sp"))
                return new Mercator(a, invF, lon0, k0, fe, fn, unit, pcsName ?? "墨卡托");

            throw new NotSupportedException("暂不支持的投影方式：" + pm.Groups[1].Value +
                                            "。可在 ArcGIS 中先投影为 CGCS2000 / WGS84 再转换。");
        }

        // 无 .prj 时根据坐标范围猜测
        public static ICoordTransform Guess(ShpFile shp, List<string> warnings)
        {
            if (shp.MinX >= -180 && shp.MaxX <= 180 && shp.MinY >= -90 && shp.MaxY <= 90)
            {
                warnings.Add("没有 .prj 文件，按坐标范围判断为经纬度坐标。");
                return new Geographic();
            }
            if (shp.MinX > 1000000)
            {
                int zone = (int)(shp.MinX / 1000000);
                double cm = -1;
                string kind = null;
                if (zone >= 25 && zone <= 45) { cm = zone * 3; kind = "3度带"; }
                else if (zone >= 13 && zone <= 23) { cm = zone * 6 - 3; kind = "6度带"; }
                if (cm > 0)
                {
                    warnings.Add("没有 .prj 文件，按坐标中的带号 " + zone + " 判断为 CGCS2000 " + kind +
                                 "（中央经线 " + cm + "°）。");
                    return GaussKruger(cm, shp.MinX);
                }
            }
            throw new NotSupportedException("缺少 .prj 文件，且无法从坐标判断坐标系。请在『坐标系』中选择高斯投影并填写中央经线。");
        }

        public static ICoordTransform GaussKruger(double cm, double sampleX)
        {
            double fe = sampleX > 1000000 ? Math.Floor(sampleX / 1000000) * 1000000 + 500000 : 500000;
            return new TransverseMercator(CgcsA, CgcsInvF, cm, 0, 1, fe, 0, 1,
                "CGCS2000 高斯-克吕格（中央经线 " + cm.ToString(Inv) + "°，东偏 " + fe.ToString(Inv) + "）");
        }

        static void CheckDatum(string wkt, List<string> warnings)
        {
            string datum = Match(wkt, @"DATUM\[\s*""([^""]*)""");
            if (datum == null) return;
            string d = datum.ToLowerInvariant();
            if (d.Contains("wgs") || d.Contains("2000") || d.Contains("cgcs") || d.Contains("china")) return;
            if (d.Contains("xian") || d.Contains("1980") || d.Contains("beijing") || d.Contains("1954"))
                warnings.Add("坐标基准为 " + datum + "，本软件未做七参数转换，在 Google 地球中可能偏移数十至上百米。");
            else
                warnings.Add("坐标基准为 " + datum + "，按与 WGS84 相同处理，可能存在偏移。");
        }

        static string Match(string s, string pattern)
        {
            Match m = Regex.Match(s, pattern, RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        static double Get(Dictionary<string, double> p, double def, params string[] keys)
        {
            foreach (string k in keys) { double v; if (p.TryGetValue(k, out v)) return v; }
            return def;
        }
    }

    // ---------------------------------------------------------------- 转换
    public class ConvertOptions
    {
        public string NameField;          // 空 = 自动
        public string EncodingMode = "auto";
        public string CrsMode = "auto";   // auto / geographic / gk
        public double CentralMeridian = 108;
        public Color LineColor = Color.Yellow;
        public Color FillColor = Color.Yellow;
        public int FillOpacity = 20;      // 0-100
        public double LineWidth = 2;
        public bool ShowLabels = true;
        public bool IncludeAttributes = true;
    }

    public class Layer
    {
        public string Path;
        public string Name;
        public ShpFile Shp;
        public DbfTable Dbf;
        public ICoordTransform Transform;
        public int NameIndex = -1;
    }

    public static class Converter
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly string[] NameHints = { "名称", "NAME", "MC", "XMMC", "小班号", "XBH", "XIAOBAN", "小班", "编号", "BH", "图斑号", "TBBH", "DKBH", "地块编号" };

        public static Layer Load(string shpPath, ConvertOptions o, Action<string> log)
        {
            var layer = new Layer { Path = shpPath, Name = System.IO.Path.GetFileNameWithoutExtension(shpPath) };
            layer.Shp = ShpReader.Read(shpPath);

            string dbf = System.IO.Path.ChangeExtension(shpPath, ".dbf");
            if (File.Exists(dbf))
            {
                layer.Dbf = DbfTable.Read(dbf, o.EncodingMode, false);
                log("  属性编码：" + layer.Dbf.EncodingInfo + "，字段 " + layer.Dbf.Fields.Count + " 个");
            }
            else log("  未找到 .dbf 属性表，仅转换图形。");

            var warnings = new List<string>();
            if (o.CrsMode == "geographic") layer.Transform = new Geographic();
            else if (o.CrsMode == "gk") layer.Transform = Crs.GaussKruger(o.CentralMeridian, layer.Shp.MinX);
            else
            {
                string prj = System.IO.Path.ChangeExtension(shpPath, ".prj");
                if (File.Exists(prj)) layer.Transform = Crs.FromPrj(File.ReadAllText(prj), warnings);
                else layer.Transform = Crs.Guess(layer.Shp, warnings);
            }
            log("  坐标系：" + layer.Transform.Description);
            foreach (string w in warnings) log("  ⚠ " + w);

            // 校验转换结果
            double lon, lat;
            layer.Transform.ToLonLat((layer.Shp.MinX + layer.Shp.MaxX) / 2, (layer.Shp.MinY + layer.Shp.MaxY) / 2, out lon, out lat);
            if (double.IsNaN(lon) || double.IsNaN(lat) || Math.Abs(lon) > 180 || Math.Abs(lat) > 90)
                throw new InvalidDataException("坐标转换结果超出经纬度范围，坐标系设置可能不正确。");
            log("  中心点：东经 " + lon.ToString("0.0000", Inv) + "°，北纬 " + lat.ToString("0.0000", Inv) + "°");

            if (layer.Dbf != null)
            {
                if (!string.IsNullOrEmpty(o.NameField))
                {
                    layer.NameIndex = layer.Dbf.FieldIndex(o.NameField);
                    if (layer.NameIndex < 0) log("  ⚠ 未找到名称字段“" + o.NameField + "”，改为自动选择。");
                }
                if (layer.NameIndex < 0) layer.NameIndex = AutoNameField(layer.Dbf);
                if (layer.NameIndex >= 0) log("  名称字段：" + layer.Dbf.Fields[layer.NameIndex].Name);
            }
            return layer;
        }

        public static int AutoNameField(DbfTable dbf)
        {
            foreach (string h in NameHints)
                for (int i = 0; i < dbf.Fields.Count; i++)
                    if (string.Equals(dbf.Fields[i].Name, h, StringComparison.OrdinalIgnoreCase)) return i;
            foreach (string h in NameHints)
                for (int i = 0; i < dbf.Fields.Count; i++)
                    if (dbf.Fields[i].Name.ToUpperInvariant().Contains(h.ToUpperInvariant())) return i;
            return -1;
        }

        public static int WriteKmz(List<Layer> layers, string outPath, string docName, ConvertOptions o, Action<string> log)
        {
            string tmp = outPath + ".tmp";
            int total = 0;
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = zip.CreateEntry("doc.kml", CompressionLevel.Optimal);
                using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false), 1 << 16))
                {
                    w.NewLine = "\n";
                    w.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                    w.WriteLine("<kml xmlns=\"http://www.opengis.net/kml/2.2\">");
                    w.WriteLine("<Document>");
                    w.WriteLine("<name>" + Xml(docName) + "</name>");
                    w.WriteLine("<open>1</open>");
                    for (int i = 0; i < layers.Count; i++) WriteStyle(w, "s" + i, layers[i], o);
                    for (int i = 0; i < layers.Count; i++)
                    {
                        int n = WriteLayer(w, "s" + i, layers[i], o);
                        log("  " + layers[i].Name + "：写入 " + n + " 个要素");
                        total += n;
                    }
                    w.WriteLine("</Document>");
                    w.WriteLine("</kml>");
                }
            }
            if (File.Exists(outPath)) File.Delete(outPath);
            File.Move(tmp, outPath);
            return total;
        }

        static string KmlColor(Color c, int alpha)
        {
            return alpha.ToString("x2") + c.B.ToString("x2") + c.G.ToString("x2") + c.R.ToString("x2");
        }

        static void WriteStyle(TextWriter w, string id, Layer l, ConvertOptions o)
        {
            bool isPoint = l.Shp.Shapes.Exists(s => s.Kind == GeomKind.Point || s.Kind == GeomKind.MultiPoint) &&
                           !l.Shp.Shapes.Exists(s => s.Kind == GeomKind.Line || s.Kind == GeomKind.Polygon);
            int alpha = (int)Math.Round(o.FillOpacity * 2.55);
            w.WriteLine("<Style id=\"" + id + "\">");
            if (isPoint)
                w.WriteLine("<IconStyle><color>" + KmlColor(o.LineColor, 255) + "</color><scale>1</scale>" +
                            "<Icon><href>http://maps.google.com/mapfiles/kml/paddle/wht-blank.png</href></Icon></IconStyle>");
            else
                w.WriteLine("<IconStyle><scale>0</scale></IconStyle>");
            w.WriteLine("<LabelStyle><scale>" + (o.ShowLabels ? "0.9" : "0") + "</scale></LabelStyle>");
            w.WriteLine("<LineStyle><color>" + KmlColor(o.LineColor, 255) + "</color><width>" + o.LineWidth.ToString(Inv) + "</width></LineStyle>");
            w.WriteLine("<PolyStyle><color>" + KmlColor(o.FillColor, alpha) + "</color><fill>" + (alpha > 0 ? 1 : 0) + "</fill><outline>1</outline></PolyStyle>");
            w.WriteLine("<BalloonStyle><text>$[description]</text></BalloonStyle>");
            w.WriteLine("</Style>");
        }

        class Ring
        {
            public double[] Lon, Lat;
            public double Area;          // 原坐标系下的带符号面积
            public double MinX, MinY, MaxX, MaxY;
            public Part Src;
        }

        static int WriteLayer(TextWriter w, string styleId, Layer l, ConvertOptions o)
        {
            w.WriteLine("<Folder>");
            w.WriteLine("<name>" + Xml(l.Name) + "</name>");
            int count = 0;
            var sb = new StringBuilder();
            for (int i = 0; i < l.Shp.Shapes.Count; i++)
            {
                Shape s = l.Shp.Shapes[i];
                string[] rec = null;
                if (l.Dbf != null && i < l.Dbf.Records.Count)
                {
                    rec = l.Dbf.Records[i];
                    if (rec == null) continue; // 已删除
                }
                if (s.Kind == GeomKind.Null || s.Parts.Count == 0) continue;

                sb.Length = 0;
                if (!WriteGeometry(sb, s, l.Transform, o.ShowLabels)) continue;

                string name = (rec != null && l.NameIndex >= 0) ? rec[l.NameIndex] : null;
                if (string.IsNullOrEmpty(name)) name = (i + 1).ToString(Inv);

                w.WriteLine("<Placemark>");
                w.WriteLine("<name>" + Xml(name) + "</name>");
                w.WriteLine("<styleUrl>#" + styleId + "</styleUrl>");
                if (rec != null && o.IncludeAttributes)
                {
                    var d = new StringBuilder();
                    d.Append("<table border=\"1\" cellpadding=\"3\" cellspacing=\"0\" style=\"border-collapse:collapse;font-size:12px\">");
                    for (int f = 0; f < l.Dbf.Fields.Count; f++)
                        d.Append("<tr><td style=\"background:#eee\">").Append(Html(l.Dbf.Fields[f].Name))
                         .Append("</td><td>").Append(Html(rec[f])).Append("</td></tr>");
                    d.Append("</table>");
                    w.WriteLine("<description><![CDATA[" + d.ToString().Replace("]]>", "]]&gt;") + "]]></description>");
                    w.Write("<ExtendedData>");
                    for (int f = 0; f < l.Dbf.Fields.Count; f++)
                        w.Write("<Data name=\"" + Xml(l.Dbf.Fields[f].Name) + "\"><value>" + Xml(rec[f]) + "</value></Data>");
                    w.WriteLine("</ExtendedData>");
                }
                w.Write(sb.ToString());
                w.WriteLine("</Placemark>");
                count++;
            }
            w.WriteLine("</Folder>");
            return count;
        }

        static bool WriteGeometry(StringBuilder sb, Shape s, ICoordTransform t, bool label)
        {
            if (s.Kind == GeomKind.Point || s.Kind == GeomKind.MultiPoint)
            {
                var pts = new List<string>();
                foreach (Part p in s.Parts)
                {
                    double lon, lat;
                    t.ToLonLat(p.X[0], p.Y[0], out lon, out lat);
                    if (Valid(lon, lat)) pts.Add("<Point><coordinates>" + C(lon, lat) + "</coordinates></Point>");
                }
                if (pts.Count == 0) return false;
                if (pts.Count == 1) sb.AppendLine(pts[0]);
                else { sb.Append("<MultiGeometry>"); foreach (string p in pts) sb.Append(p); sb.AppendLine("</MultiGeometry>"); }
                return true;
            }

            var rings = new List<Ring>();
            foreach (Part p in s.Parts)
            {
                Ring r = Project(p, t);
                if (r != null) rings.Add(r);
            }
            if (rings.Count == 0) return false;

            var geoms = new List<string>();
            double labLon = double.NaN, labLat = double.NaN;

            if (s.Kind == GeomKind.Line)
            {
                Ring longest = null; double best = -1;
                foreach (Ring r in rings)
                {
                    if (r.Lon.Length < 2) continue;
                    geoms.Add("<LineString><tessellate>1</tessellate><coordinates>" + Coords(r) + "</coordinates></LineString>");
                    double len = (r.MaxX - r.MinX) + (r.MaxY - r.MinY);
                    if (len > best) { best = len; longest = r; }
                }
                if (longest != null) { int m = longest.Lon.Length / 2; labLon = longest.Lon[m]; labLat = longest.Lat[m]; }
            }
            else
            {
                rings.RemoveAll(r => r.Lon.Length < 4 || r.Area == 0);
                if (rings.Count == 0) return false;
                // SHP 规范：外环顺时针（面积为负），内环逆时针
                var outers = rings.FindAll(r => r.Area < 0);
                var holes = rings.FindAll(r => r.Area > 0);
                if (outers.Count == 0) { outers = holes; holes = new List<Ring>(); }
                var holeOf = new Dictionary<Ring, List<Ring>>();
                foreach (Ring o in outers) holeOf[o] = new List<Ring>();
                foreach (Ring h in holes)
                {
                    Ring owner = null;
                    foreach (Ring o in outers)
                    {
                        if (h.MinX < o.MinX || h.MaxX > o.MaxX || h.MinY < o.MinY || h.MaxY > o.MaxY) continue;
                        if (Inside(o.Src, h.Src.X[0], h.Src.Y[0]) || Inside(o.Src, (h.MinX + h.MaxX) / 2, (h.MinY + h.MaxY) / 2))
                        {
                            if (owner == null || Math.Abs(o.Area) < Math.Abs(owner.Area)) owner = o;
                        }
                    }
                    if (owner != null) holeOf[owner].Add(h);
                    else { h.Area = -h.Area; outers.Add(h); holeOf[h] = new List<Ring>(); }
                }
                Ring biggest = null;
                foreach (Ring o in outers)
                {
                    var g = new StringBuilder();
                    g.Append("<Polygon><tessellate>1</tessellate><outerBoundaryIs><LinearRing><coordinates>")
                     .Append(Coords(o)).Append("</coordinates></LinearRing></outerBoundaryIs>");
                    foreach (Ring h in holeOf[o])
                        g.Append("<innerBoundaryIs><LinearRing><coordinates>").Append(Coords(h))
                         .Append("</coordinates></LinearRing></innerBoundaryIs>");
                    g.Append("</Polygon>");
                    geoms.Add(g.ToString());
                    if (biggest == null || Math.Abs(o.Area) > Math.Abs(biggest.Area)) biggest = o;
                }
                if (label && biggest != null) LabelPoint(biggest, holeOf[biggest], out labLon, out labLat);
            }

            if (geoms.Count == 0) return false;
            bool addLabel = label && Valid(labLon, labLat);
            if (geoms.Count == 1 && !addLabel) { sb.AppendLine(geoms[0]); return true; }
            sb.Append("<MultiGeometry>");
            if (addLabel) sb.Append("<Point><coordinates>").Append(C(labLon, labLat)).Append("</coordinates></Point>");
            foreach (string g in geoms) sb.Append(g);
            sb.AppendLine("</MultiGeometry>");
            return true;
        }

        static Ring Project(Part p, ICoordTransform t)
        {
            var lon = new List<double>(p.Count);
            var lat = new List<double>(p.Count);
            double area = 0;
            for (int i = 0; i < p.Count; i++)
            {
                double x = p.X[i], y = p.Y[i];
                if (double.IsNaN(x) || double.IsNaN(y)) continue;
                double lo, la;
                t.ToLonLat(x, y, out lo, out la);
                if (!Valid(lo, la)) continue;
                lon.Add(lo); lat.Add(la);
            }
            if (lon.Count == 0) return null;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
                area += (p.X[j] * p.Y[i] - p.X[i] * p.Y[j]);
            var r = new Ring { Lon = lon.ToArray(), Lat = lat.ToArray(), Area = area / 2, Src = p };
            r.MinX = r.MaxX = p.X[0]; r.MinY = r.MaxY = p.Y[0];
            for (int i = 1; i < p.Count; i++)
            {
                if (p.X[i] < r.MinX) r.MinX = p.X[i]; if (p.X[i] > r.MaxX) r.MaxX = p.X[i];
                if (p.Y[i] < r.MinY) r.MinY = p.Y[i]; if (p.Y[i] > r.MaxY) r.MaxY = p.Y[i];
            }
            return r;
        }

        static bool Inside(Part p, double x, double y)
        {
            bool c = false;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            {
                if ((p.Y[i] > y) != (p.Y[j] > y) &&
                    x < (p.X[j] - p.X[i]) * (y - p.Y[i]) / (p.Y[j] - p.Y[i]) + p.X[i]) c = !c;
            }
            return c;
        }

        static bool InsideLL(double[] xs, double[] ys, double x, double y)
        {
            bool c = false;
            for (int i = 0, j = xs.Length - 1; i < xs.Length; j = i++)
            {
                if ((ys[i] > y) != (ys[j] > y) &&
                    x < (xs[j] - xs[i]) * (y - ys[i]) / (ys[j] - ys[i]) + xs[i]) c = !c;
            }
            return c;
        }

        // 标注点：优先用质心，质心不在面内时取穿过质心的水平线上最宽的一段的中点
        static void LabelPoint(Ring outer, List<Ring> holes, out double lon, out double lat)
        {
            double[] xs = outer.Lon, ys = outer.Lat;
            double a = 0, cx = 0, cy = 0;
            for (int i = 0, j = xs.Length - 1; i < xs.Length; j = i++)
            {
                double f = xs[j] * ys[i] - xs[i] * ys[j];
                a += f; cx += (xs[j] + xs[i]) * f; cy += (ys[j] + ys[i]) * f;
            }
            if (a != 0) { cx /= 3 * a; cy /= 3 * a; }
            else { cx = xs[0]; cy = ys[0]; }

            bool ok = InsideLL(xs, ys, cx, cy);
            foreach (Ring h in holes) if (ok && InsideLL(h.Lon, h.Lat, cx, cy)) ok = false;
            if (ok) { lon = cx; lat = cy; return; }

            var cross = new List<double>();
            var all = new List<Ring> { outer };
            all.AddRange(holes);
            foreach (Ring r in all)
                for (int i = 0, j = r.Lon.Length - 1; i < r.Lon.Length; j = i++)
                    if ((r.Lat[i] > cy) != (r.Lat[j] > cy))
                        cross.Add((r.Lon[j] - r.Lon[i]) * (cy - r.Lat[i]) / (r.Lat[j] - r.Lat[i]) + r.Lon[i]);
            cross.Sort();
            double bestW = -1; lon = xs[0]; lat = ys[0];
            for (int i = 0; i + 1 < cross.Count; i += 2)
            {
                double wdt = cross[i + 1] - cross[i];
                if (wdt > bestW) { bestW = wdt; lon = (cross[i] + cross[i + 1]) / 2; lat = cy; }
            }
        }

        static bool Valid(double lon, double lat)
        {
            return !double.IsNaN(lon) && !double.IsNaN(lat) && Math.Abs(lon) <= 180 && Math.Abs(lat) <= 90;
        }

        static string C(double lon, double lat)
        {
            return lon.ToString("0.0000000", Inv) + "," + lat.ToString("0.0000000", Inv);
        }

        static string Coords(Ring r)
        {
            var sb = new StringBuilder(r.Lon.Length * 22);
            for (int i = 0; i < r.Lon.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(r.Lon[i].ToString("0.0000000", Inv)).Append(',').Append(r.Lat[i].ToString("0.0000000", Inv));
            }
            return sb.ToString();
        }

        public static string Xml(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default:
                        if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') continue;
                        sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }

        static string Html(string s)
        {
            return Xml(s);
        }
    }
}
