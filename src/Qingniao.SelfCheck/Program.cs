using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Qingniao.SelfCheck;

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch
{
    // 控制台编码设置失败(如重定向环境不支持)时忽略,继续执行自检。
}

var json = false;
string? only = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--json":
            json = true;
            break;
        case "--only":
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("错误: --only 需要一个检查名参数");
                return 2;
            }

            only = args[++i];
            break;
        default:
            Console.Error.WriteLine($"错误: 无法识别的参数 \"{args[i]}\"(支持 --json 与 --only <name>)");
            return 2;
    }
}

var checks = CheckRegistry.CreateAll();
if (only is not null)
{
    var match = checks.FirstOrDefault(c =>
        string.Equals(c.Name, only, StringComparison.OrdinalIgnoreCase));
    if (match is null)
    {
        Console.Error.WriteLine(
            $"错误: 未知检查名 \"{only}\"(可用: {string.Join(", ", checks.Select(c => c.Name))})");
        return 2;
    }

    checks = [match];
}

var results = new List<CheckResult>();
foreach (var check in checks)
{
    results.Add(await check.RunAsync(CancellationToken.None));
}

if (json)
{
    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    Console.WriteLine(JsonSerializer.Serialize(results, options));
}
else
{
    Console.WriteLine(ReportWriter.Render(results, Environment.MachineName));
}

return ExitCodeMapper.Compute(results);
