using System.Globalization;
using System.Text.Json;

namespace ApiPlayground.Services;

/// <summary>
/// Dog API（dogapi.dog / v2）调试服务：犬种列表与详情、随机犬图、上游 302 图片解析、随机趣闻、分组统计。
///
/// 上游完全免密钥（官方文档原文 "No key, no sign up"），所以这里没有任何密钥读取逻辑，
/// 也不存在 .env 泄漏风险。
///
/// 为什么要有磁盘缓存：上游 /breeds 全量响应 1.84 MB（283 个犬种，含图片与出处），
/// 国内实测单次 15~40 秒，直接每次请求都打上游会让页面首屏一直转圈。因此分三层：
///   1) 内存索引：解析后的对象，命中即微秒级返回；
///   2) 仓库自带种子 Data/dog-breeds.json（约 660 KB，首启即可用，离线也能演示）；
///   3) 运行时缓存 .cache/dog-breeds-v2.json（后台刷新成功后落盘，7 天内优先于种子）。
/// 后台刷新是单航班（Interlocked 标志），不会因为几个并发请求同时去拉 1.8 MB。
/// </summary>
public sealed class DogService
{
    private const string BaseUrl = "https://dogapi.dog/api/v2";

    /// <summary>小响应（详情、趣闻、302 头）用短超时；1.8 MB 的索引走长超时并开启压缩。</summary>
    private static readonly HttpClient Http = NewClient(TimeSpan.FromSeconds(12));
    private static readonly HttpClient Bulk = NewClient(TimeSpan.FromSeconds(120));

    private static HttpClient NewClient(TimeSpan timeout) => new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(4),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        AllowAutoRedirect = false,          // 图片端点靠 302 拿 Location，不能自动跟随
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    { Timeout = timeout, DefaultRequestHeaders = { { "User-Agent", "ApiPlayground/1.0 (+local debug)" } } };

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan MemoryTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan DiskTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(24);

    private static volatile DogBreed[]? _breeds;
    private static DateTimeOffset _breedsAt = DateTimeOffset.MinValue;
    private static string _source = "未加载";
    private static int _groupCount;      // 分组数与低敏数在索引装载时算一次，列表接口不再遍历
    private static int _hypoCount;
    private static int _refreshing;

    private static string SeedFile => Path.Combine(AppContext.BaseDirectory, "Data", "dog-breeds.json");
    private static string CacheFile => Path.Combine(AppContext.BaseDirectory, ".cache", "dog-breeds-v2.json");

    // ---------- 数据模型 ----------

    private readonly record struct Range(int? Min, int? Max)
    {
        public string Display => (Min, Max) switch
        {
            (null, null) => "",
            ({ } a, null) => a.ToString(CultureInfo.InvariantCulture),
            (null, { } b) => "~" + b.ToString(CultureInfo.InvariantCulture),
            ({ } a, { } b) => a == b
                ? a.ToString(CultureInfo.InvariantCulture)
                : a.ToString(CultureInfo.InvariantCulture) + "–" + b.ToString(CultureInfo.InvariantCulture)
        };
    }

    private sealed record DogImage(
        string Url, string Thumb, string Medium, string Large,
        string Author, string License, string LicenseUrl, string SourceUrl);

    private sealed class DogBreed
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string NameZh { get; init; } = "";
        public string GroupId { get; init; } = "";
        public string Country { get; init; } = "";
        public string Region { get; init; } = "";
        public string Era { get; init; } = "";
        public Range Life { get; init; }
        public Range MaleWeight { get; init; }
        public Range FemaleWeight { get; init; }
        public Range MaleHeight { get; init; }
        public Range FemaleHeight { get; init; }
        public bool Hypoallergenic { get; init; }
        public string CoatType { get; init; } = "";
        public string CoatLength { get; init; } = "";
        public string[] CoatColors { get; init; } = [];
        public Dictionary<string, int> Traits { get; init; } = [];
        public string[] Temperament { get; init; } = [];
        public string Description { get; init; } = "";
        public string[] OtherNames { get; init; } = [];
        public string[] RecognizedBy { get; init; } = [];
        public DogImage[] Images { get; init; } = [];

        /// <summary>展示名（「中文 · 英文」），构建索引时算好，避免列表每次访问都拼字符串。</summary>
        public string Label { get; init; } = "";

        public int? Trait(string key) => Traits.TryGetValue(key, out var v) ? v : null;
    }

    // ---------- 中文映射（未收录的一律保留英文原文，不做机翻） ----------

    private static readonly Dictionary<string, string> GroupZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Herding Group"] = "牧羊犬组",
        ["Hound Group"] = "猎犬组",
        ["Toy Group"] = "玩具犬组",
        ["Terrier Group"] = "梗犬组",
        ["Working Group"] = "工作犬组",
        ["Sporting Group"] = "运动犬组（枪猎）",
        ["Non-Sporting Group"] = "非运动犬组",
        ["Miscellaneous Class"] = "杂项类",
        ["Foundation Stock Service"] = "基础种群（FSS）",
    };

    private static readonly Dictionary<string, string> CoatTypeZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wire"] = "刚毛", ["smooth"] = "平滑", ["short"] = "短毛", ["medium"] = "中毛",
        ["long"] = "长毛", ["curly"] = "卷毛", ["double"] = "双层毛", ["corded"] = "绳状毛",
        ["hairless"] = "无毛",
    };

    private static readonly Dictionary<string, string> CoatLengthZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["short"] = "短", ["medium"] = "中", ["long"] = "长", ["hairless"] = "无",
    };

    private static readonly Dictionary<string, string> CountryZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Germany"] = "德国", ["England"] = "英格兰", ["United Kingdom"] = "英国", ["Scotland"] = "苏格兰",
        ["Wales"] = "威尔士", ["Ireland"] = "爱尔兰", ["France"] = "法国", ["Italy"] = "意大利",
        ["Spain"] = "西班牙", ["Portugal"] = "葡萄牙", ["Belgium"] = "比利时", ["Netherlands"] = "荷兰",
        ["Switzerland"] = "瑞士", ["Sweden"] = "瑞典", ["Norway"] = "挪威", ["Finland"] = "芬兰",
        ["Denmark"] = "丹麦", ["Iceland"] = "冰岛", ["Poland"] = "波兰", ["Hungary"] = "匈牙利",
        ["Czech Republic"] = "捷克", ["Slovakia"] = "斯洛伐克", ["Croatia"] = "克罗地亚",
        ["Romania"] = "罗马尼亚", ["Russia"] = "俄罗斯", ["Turkey"] = "土耳其", ["Greece"] = "希腊",
        ["China"] = "中国", ["China (Tibet)"] = "中国西藏", ["Tibet"] = "西藏", ["Japan"] = "日本",
        ["South Korea"] = "韩国", ["Taiwan"] = "中国台湾", ["Thailand"] = "泰国",
        ["United States"] = "美国", ["Canada"] = "加拿大", ["Mexico"] = "墨西哥", ["Cuba"] = "古巴",
        ["Brazil"] = "巴西", ["Argentina"] = "阿根廷", ["Peru"] = "秘鲁", ["Australia"] = "澳大利亚",
        ["New Zealand"] = "新西兰", ["South Africa"] = "南非", ["Malta"] = "马耳他",
        ["Morocco"] = "摩洛哥", ["Egypt"] = "埃及", ["Israel"] = "以色列", ["India"] = "印度",
        ["Afghanistan"] = "阿富汗", ["Central Asia"] = "中亚", ["Middle East"] = "中东",
        ["Madagascar"] = "马达加斯加", ["Zimbabwe"] = "津巴布韦",
        ["Bosnia and Herzegovina and Croatia"] = "波黑与克罗地亚",
        ["Democratic Republic of the Congo"] = "刚果（金）",
        ["Mali, Niger, Burkina Faso"] = "马里、尼日尔、布基纳法索",
    };

    private static readonly Dictionary<string, string> BreedZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Affenpinscher"] = "猴面犬", ["Afghan Hound"] = "阿富汗猎犬", ["Airedale Terrier"] = "万能梗",
        ["Akita"] = "秋田犬", ["Alaskan Klee Kai"] = "阿拉斯加克利凯犬", ["Alaskan Malamute"] = "阿拉斯加雪橇犬",
        ["American Bulldog"] = "美国斗牛犬", ["American English Coonhound"] = "美国英国猎浣熊犬",
        ["American Eskimo Dog"] = "美国爱斯基摩犬", ["American Foxhound"] = "美国猎狐犬",
        ["American Hairless Terrier"] = "美国无毛梗", ["American Leopard Hound"] = "美国豹猎犬",
        ["American Staffordshire Terrier"] = "美国斯塔福郡梗", ["American Water Spaniel"] = "美国水猎犬",
        ["Anatolian Shepherd Dog"] = "安纳托利亚牧羊犬", ["Appenzeller Sennenhund"] = "阿彭策尔山地犬",
        ["Australian Cattle Dog"] = "澳洲牧牛犬", ["Australian Kelpie"] = "澳洲凯尔皮犬",
        ["Australian Shepherd"] = "澳洲牧羊犬", ["Australian Stumpy Tail Cattle Dog"] = "澳洲断尾牧牛犬",
        ["Australian Terrier"] = "澳洲梗", ["Azawakh"] = "阿扎瓦克猎犬", ["Barbet"] = "巴尔贝犬",
        ["Basenji"] = "巴仙吉犬", ["Basset Fauve de Bretagne"] = "布列塔尼红褐短腿猎犬",
        ["Basset Hound"] = "巴吉度猎犬", ["Bavarian Mountain Scent Hound"] = "巴伐利亚山地猎犬",
        ["Beagle"] = "比格犬", ["Bearded Collie"] = "长须柯利牧羊犬", ["Beauceron"] = "博斯龙犬",
        ["Bedlington Terrier"] = "贝灵顿梗", ["Belgian Laekenois"] = "比利时拉肯努瓦犬",
        ["Belgian Malinois"] = "比利时马林诺斯犬", ["Belgian Sheepdog"] = "比利时牧羊犬",
        ["Belgian Tervuren"] = "比利时特弗伦犬", ["Bergamasco Sheepdog"] = "贝加马斯卡牧羊犬",
        ["Berger Picard"] = "皮卡牧羊犬", ["Bernese Mountain Dog"] = "伯恩山犬", ["Bichon Frise"] = "比熊犬",
        ["Biewer Terrier"] = "比维尔梗", ["Black and Tan Coonhound"] = "黑褐猎浣熊犬",
        ["Black Russian Terrier"] = "黑俄罗斯梗", ["Bloodhound"] = "寻血猎犬",
        ["Bluetick Coonhound"] = "蓝斑猎浣熊犬", ["Boerboel"] = "波尔波犬",
        ["Bohemian Shepherd"] = "波希米亚牧羊犬", ["Bolognese"] = "博洛尼亚犬",
        ["Border Collie"] = "边境牧羊犬", ["Border Terrier"] = "边境梗", ["Borzoi"] = "波索尔猎犬",
        ["Boston Terrier"] = "波士顿梗", ["Bouvier des Flandres"] = "法兰德斯牧牛犬", ["Boxer"] = "拳师犬",
        ["Boykin Spaniel"] = "博伊金猎犬", ["Bracco Italiano"] = "意大利布拉科犬",
        ["Braque du Bourbonnais"] = "波旁指示犬", ["Braque Francais Pyrenean"] = "比利牛斯法国指示犬",
        ["Briard"] = "布里亚犬", ["Brittany"] = "布列塔尼猎犬", ["Broholmer"] = "布罗霍尔姆犬",
        ["Brussels Griffon"] = "布鲁塞尔格里芬犬", ["Bulldog"] = "英国斗牛犬", ["Bullmastiff"] = "斗牛獒犬",
        ["Bull Terrier"] = "牛头梗", ["Cairn Terrier"] = "凯恩梗", ["Canaan Dog"] = "迦南犬",
        ["Cane Corso"] = "卡斯罗犬", ["Cardigan Welsh Corgi"] = "卡迪根威尔士柯基",
        ["Carolina Dog"] = "卡罗莱纳犬", ["Catahoula Leopard Dog"] = "卡塔胡拉豹犬",
        ["Caucasian Shepherd Dog"] = "高加索犬", ["Cavalier King Charles Spaniel"] = "骑士查理王小猎犬",
        ["Central Asian Shepherd Dog"] = "中亚牧羊犬", ["Cesky Terrier"] = "捷克梗",
        ["Chesapeake Bay Retriever"] = "切萨皮克湾寻回犬", ["Chihuahua"] = "吉娃娃",
        ["Chinese Crested"] = "中国冠毛犬", ["Chinese Shar-Pei"] = "沙皮犬", ["Chinook"] = "奇努克犬",
        ["Chow Chow"] = "松狮犬", ["Cirneco dell'Etna"] = "埃特纳猎犬", ["Clumber Spaniel"] = "克伦伯猎犬",
        ["Cocker Spaniel"] = "可卡犬", ["Collie"] = "柯利牧羊犬", ["Coton de Tulear"] = "图莱亚尔绒毛犬",
        ["Croatian Sheepdog"] = "克罗地亚牧羊犬", ["Curly-Coated Retriever"] = "卷毛寻回犬",
        ["Czechoslovakian Vlcak"] = "捷克斯洛伐克狼犬", ["Dachshund"] = "腊肠犬", ["Dalmatian"] = "斑点犬",
        ["Dandie Dinmont Terrier"] = "丹迪丁蒙梗", ["Danish-Swedish Farmdog"] = "丹瑞农场犬",
        ["Deutscher Wachtelhund"] = "德国夸特猎犬", ["Doberman Pinscher"] = "杜宾犬",
        ["Dogo Argentino"] = "阿根廷杜高犬", ["Dogue de Bordeaux"] = "波尔多獒犬",
        ["Drentsche Patrijshond"] = "德伦特猎犬", ["Drever"] = "德雷弗犬", ["Dutch Shepherd"] = "荷兰牧羊犬",
        ["English Cocker Spaniel"] = "英国可卡犬", ["English Foxhound"] = "英国猎狐犬",
        ["English Setter"] = "英国雪达犬", ["English Springer Spaniel"] = "英国史宾格犬",
        ["English Toy Spaniel"] = "英国玩具猎犬", ["Entlebucher Mountain Dog"] = "恩特勒布赫山地犬",
        ["Estrela Mountain Dog"] = "埃什特雷拉山地犬", ["Eurasier"] = "欧亚犬", ["Field Spaniel"] = "田野小猎犬",
        ["Finnish Lapphund"] = "芬兰拉普猎犬", ["Finnish Spitz"] = "芬兰猎犬",
        ["Flat-Coated Retriever"] = "平毛寻回犬", ["French Bulldog"] = "法国斗牛犬",
        ["French Spaniel"] = "法国猎犬", ["German Longhaired Pointer"] = "德国长毛指示犬",
        ["German Pinscher"] = "德国宾莎犬", ["German Shepherd Dog"] = "德国牧羊犬",
        ["German Shorthaired Pointer"] = "德国短毛指示犬", ["German Spitz"] = "德国狐狸犬",
        ["German Wirehaired Pointer"] = "德国刚毛指示犬", ["Giant Schnauzer"] = "巨型雪纳瑞",
        ["Glen of Imaal Terrier"] = "伊马尔山谷梗", ["Golden Retriever"] = "金毛寻回犬",
        ["Gordon Setter"] = "戈登雪达犬", ["Grand Basset Griffon Vendéen"] = "大巴塞特格里芬旺代犬",
        ["Great Dane"] = "大丹犬", ["Greater Swiss Mountain Dog"] = "大瑞士山地犬",
        ["Great Pyrenees"] = "大白熊犬", ["Greyhound"] = "灵缇", ["Hamiltonstovare"] = "汉密尔顿猎犬",
        ["Hanoverian Scenthound"] = "汉诺威猎犬", ["Harrier"] = "哈利犬", ["Havanese"] = "哈瓦那犬",
        ["Hokkaido"] = "北海道犬", ["Hovawart"] = "霍夫瓦尔特犬", ["Ibizan Hound"] = "伊比赞猎犬",
        ["Icelandic Sheepdog"] = "冰岛牧羊犬", ["Irish Red and White Setter"] = "爱尔兰红白雪达犬",
        ["Irish Setter"] = "爱尔兰雪达犬", ["Irish Terrier"] = "爱尔兰梗",
        ["Irish Water Spaniel"] = "爱尔兰水猎犬", ["Irish Wolfhound"] = "爱尔兰猎狼犬",
        ["Italian Greyhound"] = "意大利灵缇", ["Jagdterrier"] = "德国猎梗",
        ["Japanese Akitainu"] = "日本秋田犬", ["Japanese Chin"] = "日本狆", ["Japanese Spitz"] = "日本银狐犬",
        ["Japanese Terrier"] = "日本梗", ["Jindo"] = "珍岛犬", ["Kai Ken"] = "甲斐犬",
        ["Karelian Bear Dog"] = "卡累利阿熊犬", ["Keeshond"] = "荷兰毛狮犬",
        ["Kerry Blue Terrier"] = "凯利蓝梗", ["Kishu Ken"] = "纪州犬", ["Komondor"] = "科蒙多犬",
        ["Kromfohrlander"] = "克罗姆费兰德犬", ["Kuvasz"] = "库瓦兹犬",
        ["Labrador Retriever"] = "拉布拉多寻回犬", ["Lagotto Romagnolo"] = "拉戈托罗马涅罗犬",
        ["Lakeland Terrier"] = "湖畔梗", ["Lancashire Heeler"] = "兰开夏赫勒犬",
        ["Lapponian Herder"] = "拉普兰牧鹿犬", ["Leonberger"] = "兰伯格犬", ["Lhasa Apso"] = "拉萨犬",
        ["Löwchen"] = "小狮子犬", ["Maltese"] = "马尔济斯犬", ["Manchester Terrier (Standard)"] = "曼彻斯特梗",
        ["Manchester Terrier (Toy)"] = "曼彻斯特玩具梗", ["Mastiff"] = "马士提夫獒犬",
        ["Miniature American Shepherd"] = "迷你美国牧羊犬", ["Miniature Bull Terrier"] = "迷你牛头梗",
        ["Miniature Pinscher"] = "迷你宾莎犬", ["Miniature Schnauzer"] = "迷你雪纳瑞",
        ["Mountain Cur"] = "山地库尔犬", ["Mudi"] = "穆迪犬",
        ["Nederlandse Kooikerhondje"] = "荷兰科伊克猎犬", ["Neapolitan Mastiff"] = "那不勒斯獒",
        ["Newfoundland"] = "纽芬兰犬", ["Norfolk Terrier"] = "诺福克梗",
        ["Norrbottenspets"] = "北博滕猎犬", ["Norwegian Buhund"] = "挪威布哈德犬",
        ["Norwegian Elkhound"] = "挪威猎麋犬", ["Norwegian Lundehund"] = "挪威伦德猎犬",
        ["Norwich Terrier"] = "诺维奇梗",
        ["Nova Scotia Duck Tolling Retriever"] = "新斯科舍诱鸭寻回犬",
        ["Old English Sheepdog"] = "古代英国牧羊犬", ["Otterhound"] = "水獭猎犬", ["Papillon"] = "蝴蝶犬",
        ["Parson Russell Terrier"] = "帕森罗素梗", ["Pekingese"] = "北京犬",
        ["Pembroke Welsh Corgi"] = "彭布罗克威尔士柯基", ["Perro de Presa Canario"] = "加纳利犬",
        ["Peruvian Inca Orchid"] = "秘鲁无毛犬", ["Petit Basset Griffon Vendéen"] = "小巴塞特格里芬旺代犬",
        ["Pharaoh Hound"] = "法老王猎犬", ["Plott"] = "普罗特猎犬", ["Pointer"] = "指示犬",
        ["Polish Lowland Sheepdog"] = "波兰低地牧羊犬", ["Pomeranian"] = "博美犬",
        ["Poodle (Miniature)"] = "迷你贵宾犬", ["Poodle (Standard)"] = "标准贵宾犬",
        ["Poodle (Toy)"] = "玩具贵宾犬", ["Porcelaine"] = "波塞莱恩猎犬",
        ["Portuguese Podengo"] = "葡萄牙波登戈犬", ["Portuguese Podengo Pequeno"] = "葡萄牙小波登戈犬",
        ["Portuguese Pointer"] = "葡萄牙指示犬", ["Portuguese Sheepdog"] = "葡萄牙牧羊犬",
        ["Portuguese Water Dog"] = "葡萄牙水犬", ["Pudelpointer"] = "普德尔指示犬",
        ["Pug"] = "巴哥犬", ["Puli"] = "波利犬", ["Pumi"] = "波米犬", ["Pyrenean Mastiff"] = "比利牛斯獒",
        ["Pyrenean Shepherd"] = "比利牛斯牧羊犬", ["Rafeiro do Alentejo"] = "阿连特茹牧羊犬",
        ["Rat Terrier"] = "捕鼠梗", ["Redbone Coonhound"] = "红骨猎浣熊犬",
        ["Rhodesian Ridgeback"] = "罗德西亚脊背犬",
        ["Romanian Carpathian Shepherd"] = "罗马尼亚喀尔巴阡牧羊犬",
        ["Romanian Mioritic Shepherd Dog"] = "罗马尼亚米奥里牧羊犬", ["Rottweiler"] = "罗威纳犬",
        ["Russell Terrier"] = "罗素梗", ["Russian Toy"] = "俄罗斯玩具犬",
        ["Russian Tsvetnaya Bolonka"] = "俄罗斯彩花犬", ["Saint Bernard"] = "圣伯纳犬",
        ["Saluki"] = "萨路基猎犬", ["Samoyed"] = "萨摩耶犬", ["Schapendoes"] = "荷兰斯哈彭多斯犬",
        ["Schipperke"] = "西帕基犬", ["Scottish Deerhound"] = "苏格兰猎鹿犬",
        ["Scottish Terrier"] = "苏格兰梗", ["Sealyham Terrier"] = "西里汉梗",
        ["Segugio Italiano"] = "意大利赛古乔猎犬", ["Shetland Sheepdog"] = "喜乐蒂牧羊犬",
        ["Shiba Inu"] = "柴犬", ["Shih Tzu"] = "西施犬", ["Shikoku"] = "四国犬",
        ["Siberian Husky"] = "西伯利亚雪橇犬", ["Silky Terrier"] = "澳洲丝毛梗",
        ["Skye Terrier"] = "天空梗", ["Sloughi"] = "斯洛基猎犬",
        ["Slovakian Wirehaired Pointer"] = "斯洛伐克刚毛指示犬", ["Slovensky Cuvac"] = "斯洛伐克库瓦克犬",
        ["Slovensky Kopov"] = "斯洛伐克猎犬", ["Small Munsterlander"] = "小芒斯特兰犬",
        ["Smooth Fox Terrier"] = "平毛猎狐梗", ["Soft Coated Wheaten Terrier"] = "软毛麦色梗",
        ["Spanish Mastiff"] = "西班牙獒", ["Spanish Water Dog"] = "西班牙水犬",
        ["Spinone Italiano"] = "意大利斯皮诺犬", ["Stabyhoun"] = "斯塔比荷犬",
        ["Staffordshire Bull Terrier"] = "斯塔福郡斗牛梗", ["Standard Schnauzer"] = "标准雪纳瑞",
        ["Sussex Spaniel"] = "萨塞克斯猎犬", ["Swedish Lapphund"] = "瑞典拉普猎犬",
        ["Swedish Vallhund"] = "瑞典柯基犬", ["Taiwan Dog"] = "台湾犬",
        ["Teddy Roosevelt Terrier"] = "泰迪罗斯福梗", ["Thai Ridgeback"] = "泰国脊背犬",
        ["Tibetan Mastiff"] = "藏獒", ["Tibetan Spaniel"] = "西藏猎犬", ["Tibetan Terrier"] = "西藏梗",
        ["Tornjak"] = "托尔尼亚克犬", ["Tosa"] = "土佐犬", ["Toy Fox Terrier"] = "玩具猎狐梗",
        ["Transylvanian Hound"] = "特兰西瓦尼亚猎犬",
        ["Treeing Tennessee Brindle"] = "田纳西布林德犬", ["Treeing Walker Coonhound"] = "沃克猎浣熊犬",
        ["Vizsla"] = "维兹拉犬", ["Volpino Italiano"] = "意大利沃尔皮诺犬", ["Weimaraner"] = "魏玛犬",
        ["Welsh Springer Spaniel"] = "威尔士史宾格犬", ["Welsh Terrier"] = "威尔士梗",
        ["West Highland White Terrier"] = "西高地白梗", ["Wetterhoun"] = "荷兰水犬",
        ["Whippet"] = "小灵缇", ["Wire Fox Terrier"] = "刚毛猎狐梗",
        ["Wirehaired Pointing Griffon"] = "刚毛指示格里芬犬", ["Wirehaired Vizsla"] = "刚毛维兹拉犬",
        ["Working Kelpie"] = "工作凯尔皮犬", ["Xoloitzcuintli"] = "墨西哥无毛犬",
        ["Yakutian Laika"] = "雅库特莱卡犬", ["Yorkshire Terrier"] = "约克夏梗",
    };

    /// <summary>性格/适配度评分项：上游统一 1–5 分，exercise_minutes 是分钟数。</summary>
    private static readonly (string Key, string Label)[] TraitKeys =
    [
        ("energy", "精力"), ("trainability", "可训度"), ("barking", "吠叫倾向"), ("grooming", "美容需求"),
        ("shedding", "掉毛程度"), ("drooling", "流涎"), ("good_with_children", "亲和儿童"),
        ("good_with_dogs", "亲和其他狗"), ("good_with_strangers", "亲陌生人"),
        ("apartment_friendly", "公寓友好"), ("exercise_minutes", "每日运动"),
    ];

    // ---------- 对外方法 ----------

    /// <summary>给 /api/health 用：只报状态，不含任何敏感信息（本来也没有密钥）。</summary>
    public static object ConfigStatus() => new
    {
        upstream = "dogapi.dog /v2",
        keyRequired = false,
        breedsIndexed = _breeds?.Length ?? 0,
        indexSource = _source,
        indexFetchedAt = _breedsAt == DateTimeOffset.MinValue ? null : _breedsAt.ToString("O"),
        note = "官方免密钥；犬种索引为「内置种子 + 7 天磁盘缓存 + 后台单航班刷新」，冷启动不等上游"
    };

    /// <summary>
    /// 犬种列表：支持名称/中文名/别名/原产国关键字、按分组过滤、只看低敏，可限制条数（0 = 全部）。
    /// </summary>
    public async Task<object> BreedsAsync(string? query, string? group, string? limitRaw, string? hypoRaw)
    {
        var limit = ParseLimit(limitRaw, 0, 400, allowZero: true);
        var breeds = await EnsureAsync();

        var needle = (query ?? "").Trim();
        var groupKey = (group ?? "").Trim();
        var onlyHypo = ParseBool(hypoRaw);

        IEnumerable<DogBreed> seq = breeds;
        if (needle.Length > 0) seq = seq.Where(b => Matches(b, needle));
        if (groupKey.Length > 0) seq = seq.Where(b => InGroup(b, groupKey));
        if (onlyHypo) seq = seq.Where(b => b.Hypoallergenic);

        var matched = seq.ToArray();
        var shown = limit > 0 ? matched.Take(limit).ToArray() : matched;
        var age = DateTimeOffset.UtcNow - _breedsAt;

        return new
        {
            total = breeds.Length,
            returned = shown.Length,
            matched = matched.Length,
            items = shown.Select(b => new
            {
                id = b.Id,
                name = b.Name,
                nameZh = b.NameZh,
                label = b.Label,
                group = GroupEnOf(b.GroupId),
                groupZh = GroupZhOf(b.GroupId),
                origin = b.Country,
                originZh = CountryZhOf(b.Country),
                lifeYears = b.Life.Display,
                weightKg = b.MaleWeight.Display,
                hypoallergenic = b.Hypoallergenic,
                thumb = b.Images.Length > 0 ? b.Images[0].Thumb : "",
                imageCount = b.Images.Length
            }).ToArray(),
            groupCount = _groupCount,
            hypoallergenicCount = _hypoCount,
            source = _source,
            cached = _source != "live",
            indexAgeHours = Math.Round(age.TotalHours, 1),
            provider = "dogapi.dog /v2/breeds",
            note = _source == "live"
                ? "来自 dogapi.dog 实时索引"
                : _source == "cache"
                    ? "命中本地磁盘缓存，后台正在按需刷新"
                    : "使用仓库内置种子数据（离线可用），后台正在尝试刷新"
        };
    }

    /// <summary>单个犬种全量详情：可用 UUID、英文名或中文名查询。</summary>
    public async Task<object> BreedAsync(string? key)
    {
        var needle = (key ?? "").Trim();
        if (needle.Length == 0)
            throw new ApiException("MISSING_BREED", "请提供犬种 ID 或英文名，例如 /api/dogs/breeds/Akita");
        if (needle.Length > 80)
            throw new ApiException("INVALID_BREED", "犬种 ID 或名称过长（上限 80 字符）");

        DogBreed? hit = null;
        try { hit = Find(await EnsureAsync(), needle); }
        catch (ApiException) { /* 索引不可用时，若给的是 UUID 就直接问上游 */ }

        var live = false;
        if (hit is null && LooksLikeUuid(needle) && await FetchOneAsync(needle) is { } one)
        {
            hit = one;
            live = true;
        }
        if (hit is null)
            throw new ApiException("DOG_BREED_NOT_FOUND",
                $"未找到犬种「{needle}」，可先用 /api/dogs/breeds?q= 搜索英文名");

        var age = DateTimeOffset.UtcNow - _breedsAt;
        return new
        {
            id = hit.Id,
            name = hit.Name,
            nameZh = hit.NameZh,
            label = hit.Label,
            group = new { id = hit.GroupId, name = GroupEnOf(hit.GroupId), nameZh = GroupZhOf(hit.GroupId) },
            origin = new
            {
                country = hit.Country,
                countryZh = CountryZhOf(hit.Country),
                region = hit.Region,
                era = hit.Era
            },
            lifeYears = hit.Life.Display,
            lifeRange = RangeShape(hit.Life),
            weightKg = new
            {
                male = hit.MaleWeight.Display,
                female = hit.FemaleWeight.Display,
                maleRange = RangeShape(hit.MaleWeight),
                femaleRange = RangeShape(hit.FemaleWeight)
            },
            heightCm = new
            {
                male = hit.MaleHeight.Display,
                female = hit.FemaleHeight.Display
            },
            hypoallergenic = hit.Hypoallergenic,
            coat = new
            {
                type = hit.CoatType,
                typeZh = CoatTypeOf(hit.CoatType),
                length = hit.CoatLength,
                lengthZh = CoatLengthOf(hit.CoatLength),
                colors = hit.CoatColors
            },
            traits = TraitShape(hit),
            temperament = hit.Temperament,
            description = hit.Description,
            otherNames = hit.OtherNames,
            recognizedBy = hit.RecognizedBy,
            imageCount = hit.Images.Length,
            images = hit.Images.Select(ImageShape).ToArray(),
            source = live ? "live" : _source,
            indexAgeHours = live ? (double?)null : Math.Round(age.TotalHours, 1),
            provider = "dogapi.dog /v2/breeds/{id}",
            note = "图片授权信息见 images[].attribution，来自 Wikimedia Commons，展示时需保留署名"
        };
    }

    /// <summary>随机犬图：从索引里挑一个带图的犬种，返回品种信息 + 该尺寸图片直链 + 署名。</summary>
    public async Task<object> RandomAsync(string? breedKey, string? sizeRaw)
    {
        var size = ParseSize(sizeRaw) ?? "medium";
        var breeds = await EnsureAsync();

        var pool = breeds.Where(b => b.Images.Length > 0).ToArray();
        var key = (breedKey ?? "").Trim();
        if (key.Length > 0)
        {
            var one = Find(pool, key)
                      ?? throw new ApiException("DOG_BREED_NOT_FOUND", $"未找到带图片的犬种「{key}」");
            pool = [one];
        }
        if (pool.Length == 0)
            throw new ApiException("DOG_NO_IMAGE", "本地索引里没有可用的犬种图片，请稍后重试");

        var breed = pool[Random.Shared.Next(pool.Length)];
        var image = breed.Images[Random.Shared.Next(breed.Images.Length)];
        var url = size switch
        {
            "thumb" => image.Thumb,
            "large" => image.Large,
            "full" => image.Url,
            _ => image.Medium
        };

        return new
        {
            breed = new
            {
                id = breed.Id,
                name = breed.Name,
                nameZh = breed.NameZh,
                label = breed.Label,
                group = GroupZhOf(breed.GroupId),
                origin = CountryZhOf(breed.Country),
                lifeYears = breed.Life.Display,
                weightKg = breed.MaleWeight.Display,
                hypoallergenic = breed.Hypoallergenic
            },
            image = new
            {
                size,
                url,
                thumb = image.Thumb,
                medium = image.Medium,
                large = image.Large,
                full = image.Url,
                attribution = new
                {
                    author = image.Author,
                    license = image.License,
                    licenseUrl = image.LicenseUrl,
                    sourceUrl = image.SourceUrl
                }
            },
            imageCount = breed.Images.Length,
            poolSize = pool.Length,
            indexTotal = breeds.Length,
            source = _source,
            provider = "dogapi.dog /v2/breeds（本地索引随机取图）",
            note = "图片直链来自 images.dogapi.dog，取自 Wikimedia Commons，展示需保留作者与许可"
        };
    }

    /// <summary>
    /// 上游图片端点解析：/breeds/image 与 /breeds/{id}/image 都是 302 重定向，
    /// 这里不跟随，直接把 Location 取回来给页面用，一次往返即可。
    /// </summary>
    public async Task<object> ImageAsync(string? breedKey, string? sizeRaw)
    {
        var size = ParseSize(sizeRaw);
        var key = (breedKey ?? "").Trim();

        string path, label;
        if (key.Length > 0)
        {
            DogBreed? hit = null;
            try { hit = Find(await EnsureAsync(), key); } catch (ApiException) { /* 索引不可用时只接受 UUID */ }
            if (hit is null)
            {
                if (!LooksLikeUuid(key))
                    throw new ApiException("DOG_BREED_NOT_FOUND", $"未找到犬种「{key}」，可用英文名或 UUID 指定");
                path = "/breeds/" + Uri.EscapeDataString(key) + "/image";
                label = key;
            }
            else
            {
                path = "/breeds/" + hit.Id + "/image";
                label = hit.Label;
            }
        }
        else
        {
            path = "/breeds/image";
            label = "随机犬种";
        }
        if (size is not null) path += "?size=" + size;

        using var resp = await SendAsync(path, heavy: false);
        var status = (int)resp.StatusCode;

        if (status == 404)
            throw new ApiException("DOG_IMAGE_NOT_FOUND",
                $"上游没有为「{label}」提供图片（404），可换一个犬种试试");
        if (status != 302 && status != 301 && status != 307 && status != 308)
            resp.EnsureSuccessStatusCode();

        var location = resp.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(location))
            throw new ApiException("DOG_IMAGE_UNRESOLVED", "上游返回了重定向状态但没有 Location 头，无法解析图片地址");

        return new
        {
            breed = label,
            size = size ?? "medium",
            imageUrl = location,
            httpStatus = status,
            provider = "dogapi.dog " + path,
            note = "上游 302 已按原样解析；该地址可直接作为 <img src> 使用"
        };
    }

    /// <summary>随机趣闻：上游标了 no-store，所以不做缓存，每次都是新的。</summary>
    public async Task<object> FactsAsync(string? limitRaw)
    {
        var limit = ParseLimit(limitRaw, 1, 5);
        using var resp = await SendAsync("/facts?limit=" + limit, heavy: false);
        using var doc = await ReadJsonAsync(resp);

        var facts = new List<object>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in data.EnumerateArray())
            {
                var body = Text(e.GetOptional("attributes"), "body");
                if (string.IsNullOrWhiteSpace(body)) continue;
                facts.Add(new { id = Text(e, "id") ?? "", body });
            }
        }
        if (facts.Count == 0)
            throw new ApiException("DOG_NO_FACT", "上游没有返回趣闻内容，请稍后重试");

        return new
        {
            count = facts.Count,
            requested = limit,
            facts,
            cached = false,
            provider = "dogapi.dog /v2/facts",
            note = "上游 Cache-Control: no-store，每次请求内容都不同"
        };
    }

    /// <summary>分组统计：直接由索引聚合，不额外请求上游。</summary>
    public async Task<object> GroupsAsync()
    {
        var breeds = await EnsureAsync();
        var rows = breeds
            .GroupBy(b => b.GroupId)
            .Select(g => new
            {
                id = g.Key,
                name = GroupEnOf(g.Key),
                nameZh = GroupZhOf(g.Key),
                breedCount = g.Count(),
                hypoallergenicCount = g.Count(x => x.Hypoallergenic)
            })
            .OrderByDescending(x => x.breedCount)
            .ToArray();

        return new
        {
            total = rows.Length,
            breedTotal = breeds.Length,
            items = rows,
            source = _source,
            provider = "来自本地索引聚合（等价于 /v2/groups）",
            note = "分组名按 AKC 分组习惯译成中文，未收录的保留英文"
        };
    }

    /// <summary>启动预热：磁盘缓存或种子先顶上，若还不是实时数据就在后台补一次。</summary>
    public static async Task WarmupAsync()
    {
        try
        {
            await EnsureAsync();
            if (_source != "live") KickRefresh();
        }
        catch { /* 预热失败不影响服务启动 */ }
    }

    // ---------- 索引加载 ----------

    private static async Task<DogBreed[]> EnsureAsync()
    {
        var hot = _breeds;
        if (hot is not null)
        {
            if (DateTimeOffset.UtcNow - _breedsAt < MemoryTtl) return hot;
            KickRefresh();                       // 过期先给旧数据，后台换新的
            return hot;
        }

        await Gate.WaitAsync();
        try
        {
            if (_breeds is not null) return _breeds;

            // 1) 运行时缓存：后台刷新成功后落盘的快照
            if (TryLoadFile(CacheFile, "cache", DiskTtl) is { } cached) return cached;

            // 2) 仓库种子：首启/离线都能立刻出数据
            if (TryLoadFile(SeedFile, "seed", null) is { } seed)
            {
                KickRefresh();
                return seed;
            }

            // 3) 都没有才同步等上游（首次从裸目录启动才会走到这里）
            if (await RefreshAsync() is { } fetched) return fetched;

            throw new ApiException("DOG_UPSTREAM_UNAVAILABLE",
                "无法获取 dogapi.dog 犬种索引（超时或响应异常），且本地没有可用种子或缓存；请检查网络后重试");
        }
        finally { Gate.Release(); }
    }

    /// <summary>把索引发布出去：派生统计先算完，再写 _breeds（volatile），保证读侧不会看到计数与数据不一致。</summary>
    private static void Adopt(DogBreed[] breeds, DateTimeOffset at, string source)
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hypo = 0;
        foreach (var b in breeds)
        {
            groups.Add(b.GroupId);
            if (b.Hypoallergenic) hypo++;
        }

        _groupCount = groups.Count;
        _hypoCount = hypo;
        _breedsAt = at;
        _source = source;
        _breeds = breeds;
    }

    private static DogBreed[]? TryLoadFile(string path, string source, TimeSpan? maxAge)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (maxAge is { } ttl && DateTimeOffset.UtcNow - new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) > ttl)
                return null;

            // 流式解析：缓存文件 1.84 MB，ReadAllBytes 会先分配一个整块数组再解析，纯属浪费
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                              64 * 1024, FileOptions.SequentialScan);
            using var doc = JsonDocument.Parse(stream);
            var breeds = ParseBreeds(doc.RootElement);
            if (breeds.Length == 0) return null;

            Adopt(breeds, new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero), source);
            return breeds;
        }
        catch { return null; }
    }

    private static void KickRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(); }
            catch { /* 后台刷新失败就继续用旧数据 */ }
            finally { Volatile.Write(ref _refreshing, 0); }
        });
    }

    private static async Task<DogBreed[]?> RefreshAsync()
    {
        try
        {
            // page[size] 上限 1000，一次拿全（283 条）；开 gzip 后传输量约 1/5
            using var resp = await SendAsync("/breeds?page%5Bsize%5D=1000", heavy: true);
            using var doc = await ReadJsonAsync(resp);

            var breeds = ParseBreeds(doc.RootElement);
            if (breeds.Length == 0) return null;

            Adopt(breeds, DateTimeOffset.UtcNow, "live");

            TrySaveCache(doc.RootElement);
            return breeds;
        }
        catch { return null; }
    }

    private static void TrySaveCache(JsonElement root)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            var json = JsonSerializer.Serialize(root);
            var tmp = CacheFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, CacheFile, overwrite: true);
        }
        catch { /* 只读目录或磁盘满时静默放弃，不影响内存索引 */ }
    }

    private static async Task<DogBreed?> FetchOneAsync(string id)
    {
        using var resp = await SendAsync("/breeds/" + Uri.EscapeDataString(id), heavy: false);
        if ((int)resp.StatusCode == 404) return null;
        using var doc = await ReadJsonAsync(resp);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        return ParseBreed(data);
    }

    private static async Task<HttpResponseMessage> SendAsync(string relativeUrl, bool heavy)
    {
        try
        {
            var resp = await (heavy ? Bulk : Http).GetAsync(BaseUrl + relativeUrl);
            var status = (int)resp.StatusCode;
            if (status == 429)
            {
                resp.Dispose();
                throw new ApiException("DOG_RATE_LIMIT", "dogapi.dog 触发限流（429），请稍后再试");
            }
            if (status >= 500)
            {
                resp.Dispose();
                throw new ApiException("DOG_UPSTREAM_ERROR", $"dogapi.dog 返回 {status}，上游暂时不可用");
            }
            return resp;
        }
        catch (ApiException) { throw; }
        catch (TaskCanceledException)
        {
            throw new ApiException("DOG_UPSTREAM_TIMEOUT", "连接 dogapi.dog 超时，请检查网络后重试");
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException("DOG_UPSTREAM_UNAVAILABLE", "无法连接 dogapi.dog：" + ex.Message);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage resp)
    {
        if (!resp.IsSuccessStatusCode)
        {
            var status = (int)resp.StatusCode;
            throw new ApiException(status == 404 ? "DOG_NOT_FOUND" : "DOG_UPSTREAM_ERROR",
                $"dogapi.dog 返回 {status}，请稍后重试");
        }
        await using var stream = await resp.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    // ---------- JSON 解析 ----------

    private static DogBreed[] ParseBreeds(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        var list = new List<DogBreed>(data.GetArrayLength());
        foreach (var e in data.EnumerateArray())
        {
            if (ParseBreed(e) is { } one) list.Add(one);
        }
        return list.ToArray();
    }

    private static DogBreed? ParseBreed(JsonElement e)
    {
        var a = e.GetOptional("attributes");
        if (a.ValueKind != JsonValueKind.Object) return null;

        var id = Text(e, "id") ?? "";
        var name = Text(a, "name") ?? "";
        if (id.Length == 0 || name.Length == 0) return null;

        var origin = a.GetOptional("origin");
        var coat = a.GetOptional("coat");
        var traits = a.GetOptional("traits");

        var traitMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (traits.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in traits.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var v))
                    traitMap[p.Name] = v;
            }
        }

        var images = new List<DogImage>();
        var imgArr = a.GetOptional("images");
        if (imgArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var im in imgArr.EnumerateArray())
            {
                var url = Text(im, "url") ?? "";
                if (url.Length == 0) continue;
                var attr = im.GetOptional("attribution");
                images.Add(new DogImage(
                    url,
                    Text(im, "thumb") ?? url,
                    Text(im, "medium") ?? url,
                    Text(im, "large") ?? url,
                    Text(attr, "author") ?? "",
                    Text(attr, "license") ?? "",
                    Text(attr, "license_url") ?? "",
                    Text(attr, "source_url") ?? ""));
            }
        }

        // 分组：优先用 relationships 里的 UUID；对不上已知表时退回种子自带的 group_name，避免显示成裸 id
        var groupId = Text(e.GetOptional("relationships").GetOptional("group").GetOptional("data"), "id") ?? "";
        if (!GroupNames.ContainsKey(groupId))
            groupId = Text(a, "group_name") is { Length: > 0 } gn ? gn : groupId;

        var nameZh = BreedZh.TryGetValue(name, out var zhValue) ? zhValue : "";
        var otherNames = Strings(a, "other_names");
        var temperament = Strings(traits, "temperament");

        return new DogBreed
        {
            Id = id,
            Name = name,
            NameZh = nameZh,
            Label = LabelOf(nameZh, name),
            GroupId = groupId,
            Country = Text(origin, "country") ?? "",
            Region = Text(origin, "region") ?? "",
            Era = Text(origin, "era") ?? "",
            Life = RangeOf(a, "life"),
            MaleWeight = RangeOf(a, "male_weight"),
            FemaleWeight = RangeOf(a, "female_weight"),
            MaleHeight = RangeOf(a, "male_height"),
            FemaleHeight = RangeOf(a, "female_height"),
            Hypoallergenic = a.GetOptional("hypoallergenic").ValueKind == JsonValueKind.True,
            CoatType = Text(coat, "type") ?? "",
            CoatLength = Text(coat, "length") ?? "",
            CoatColors = Strings(coat, "colors"),
            Traits = traitMap,
            Temperament = temperament,
            Description = Text(a, "description") ?? "",
            OtherNames = otherNames,
            RecognizedBy = Strings(a, "recognized_by"),
            Images = images.ToArray()
        };
    }

    // ---------- 查询与拼装 ----------

    private static DogBreed? Find(DogBreed[] breeds, string needle)
    {
        var byId = breeds.FirstOrDefault(b => b.Id.Equals(needle, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;

        var byName = breeds.FirstOrDefault(b => b.Name.Equals(needle, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName;

        var byZh = breeds.FirstOrDefault(b => b.NameZh.Length > 0 && b.NameZh.Equals(needle, StringComparison.Ordinal));
        if (byZh is not null) return byZh;

        var byAlias = breeds.FirstOrDefault(b =>
            b.OtherNames.Any(x => x.Equals(needle, StringComparison.OrdinalIgnoreCase)));
        if (byAlias is not null) return byAlias;

        // 最后退化成包含匹配，但只在结果唯一时采用，避免「Terrier」这类词命中一堆
        var near = breeds.Where(b =>
            b.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || (b.NameZh.Length > 0 && b.NameZh.Contains(needle, StringComparison.Ordinal))).Take(2).ToArray();
        return near.Length == 1 ? near[0] : null;
    }

    /// <summary>展示名：中文名与英文名不同时拼成「柴犬 · Shiba Inu」，否则直接用英文名。</summary>
    private static string LabelOf(string nameZh, string name) =>
        nameZh.Length > 0 && !nameZh.Equals(name, StringComparison.OrdinalIgnoreCase)
            ? nameZh + " · " + name
            : name;

    private static bool Matches(DogBreed b, string needle) =>
        b.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || (b.NameZh.Length > 0 && b.NameZh.Contains(needle, StringComparison.Ordinal))
        || b.OtherNames.Any(x => x.Contains(needle, StringComparison.OrdinalIgnoreCase))
        || b.Country.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || CountryZhOf(b.Country).Contains(needle, StringComparison.Ordinal)
        || GroupZhOf(b.GroupId).Contains(needle, StringComparison.Ordinal)
        || b.Temperament.Any(x => x.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static bool InGroup(DogBreed b, string key) =>
        b.GroupId.Equals(key, StringComparison.OrdinalIgnoreCase)
        || GroupEnOf(b.GroupId).Contains(key, StringComparison.OrdinalIgnoreCase)
        || GroupZhOf(b.GroupId).Contains(key, StringComparison.Ordinal);

    /// <summary>
    /// 上游 9 个分组的 UUID（实测自 /v2/groups）。UUID 不可构造，所以这里按已知值建表；
    /// 万一上游改版导致对不上，会回落到 relationships 里的原始 id，不会崩。
    /// </summary>
    private static readonly Dictionary<string, string> GroupNames = new()
    {
        ["8000793f-a1ae-4ec4-8d55-ef83f1f644e5"] = "Foundation Stock Service",
        ["b8e4e89d-057f-432a-9e58-0b85b29b693c"] = "Herding Group",
        ["be0147df-7755-4228-b132-2518c0c6d10d"] = "Hound Group",
        ["d4b72541-a1c6-46d7-b13c-709e148c7884"] = "Miscellaneous Class",
        ["7f6ea988-366a-4e20-b4ba-4d04274fea61"] = "Non-Sporting Group",
        ["ab110192-e41b-43ff-a630-f7aee156b33a"] = "Sporting Group",
        ["1bbf373b-1937-4e73-9863-45385daa4979"] = "Terrier Group",
        ["f56dc4b1-ba1a-4454-8ce2-bd5d41404a0c"] = "Toy Group",
        ["56081cf0-fdf2-4114-9bf7-23a3f5b6af91"] = "Working Group",
    };

    private static string GroupEnOf(string groupId) =>
        GroupNames.TryGetValue(groupId, out var v) ? v : groupId;

    private static string GroupZhOf(string groupId)
    {
        var en = GroupEnOf(groupId);
        return GroupZh.TryGetValue(en, out var zh) ? zh : "";
    }

    private static string CountryZhOf(string country) =>
        country.Length > 0 && CountryZh.TryGetValue(country, out var zh) ? zh : country;

    private static string CoatTypeOf(string type) =>
        type.Length > 0 && CoatTypeZh.TryGetValue(type, out var zh) ? zh : type;

    private static string CoatLengthOf(string length) =>
        length.Length > 0 && CoatLengthZh.TryGetValue(length, out var zh) ? zh : length;

    private static object[] TraitShape(DogBreed b) => TraitKeys
        .Where(t => b.Trait(t.Key) is not null)
        .Select(t =>
        {
            var score = b.Trait(t.Key)!.Value;
            var isMinutes = t.Key == "exercise_minutes";
            return (object)new
            {
                key = t.Key,
                label = t.Label,
                score,
                max = isMinutes ? 120 : 5,
                unit = isMinutes ? "分钟" : "分",
                percent = isMinutes ? Math.Min(100, (int)Math.Round(score / 120.0 * 100)) : score * 20
            };
        }).ToArray();

    private static object? RangeShape(Range r) => r.Min is null && r.Max is null
        ? null
        : new { min = r.Min, max = r.Max, display = r.Display };

    private static object ImageShape(DogImage i) => new
    {
        url = i.Url,
        thumb = i.Thumb,
        medium = i.Medium,
        large = i.Large,
        attribution = new
        {
            author = i.Author,
            license = i.License,
            licenseUrl = i.LicenseUrl,
            sourceUrl = i.SourceUrl
        }
    };

    // ---------- 参数与 JSON 小工具 ----------

    private static int ParseLimit(string? raw, int min, int max, bool allowZero = false)
    {
        if (string.IsNullOrWhiteSpace(raw)) return allowZero && min == 0 ? 0 : min;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            throw new ApiException("INVALID_LIMIT", $"limit 必须是整数（{min}~{max}）");
        if (n < min || n > max)
            throw new ApiException("INVALID_LIMIT", $"limit 超出范围（{min}~{max}）");
        return n;
    }

    private static bool ParseBool(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        return s is "1" or "true" or "yes" or "on";
    }

    private static string? ParseSize(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return null;
        return s switch
        {
            "thumb" or "t" => "thumb",
            "medium" or "m" or "med" => "medium",
            "large" or "l" => "large",
            "full" or "f" => "full",
            _ => throw new ApiException("INVALID_SIZE", "size 只能是 thumb / medium / large / full")
        };
    }

    private static bool LooksLikeUuid(string s) =>
        s.Length == 36 && s.Count(c => c == '-') == 4;

    private static string[] Strings(JsonElement e, string key)
    {
        var arr = e.GetOptional(key);
        if (arr.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>();
        foreach (var v in arr.EnumerateArray())
        {
            if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s) list.Add(s);
        }
        return list.ToArray();
    }

    private static Range RangeOf(JsonElement e, string key)
    {
        var r = e.GetOptional(key);
        if (r.ValueKind != JsonValueKind.Object) return new Range(null, null);
        return new Range(Int(r, "min"), Int(r, "max"));
    }

    private static string? Text(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        return e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static int? Int(JsonElement e, string key)
        => e.ValueKind == JsonValueKind.Object
           && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n : null;
}
