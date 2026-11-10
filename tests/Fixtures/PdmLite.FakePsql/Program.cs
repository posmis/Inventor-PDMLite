using System.Text;
using System.Text.Json;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;
var sql = Console.In.ReadToEnd();
var capture = Environment.GetEnvironmentVariable("PDMLITE_FAKE_PSQL_CAPTURE") ?? throw new InvalidOperationException("Missing test capture directory");
Directory.CreateDirectory(capture);
File.WriteAllText(Path.Combine(capture, Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(new
{
    arguments = args,
    password = Environment.GetEnvironmentVariable("PGPASSWORD"),
    sql
}));
if (int.TryParse(Environment.GetEnvironmentVariable("PDMLITE_FAKE_PSQL_EXIT"), out var exit) && exit != 0)
{
    Console.Error.WriteLine(sql + " " + Environment.GetEnvironmentVariable("PGPASSWORD"));
    return exit;
}
if (sql.Contains("FROM pg_roles") && Environment.GetEnvironmentVariable("PDMLITE_FAKE_ROLE_EXISTS") == "1") Console.WriteLine("1");
return 0;
