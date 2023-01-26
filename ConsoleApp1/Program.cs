// See https://aka.ms/new-console-template for more information

using System.Net;
using Slon;
using Slon.Protocol.PgV3;

var protocolOptions = new PgV3ProtocolOptions();
var options = new SlonDataSourceOptions
{
    EndPoint = IPEndPoint.Parse("127.0.0.1:5432"),
    Username = "test",
    Password = "test",
    Database = "test",
    PoolSize = 10
};

var dataSource = new SlonDataSource(options, protocolOptions);

var connection = new SlonConnection(dataSource);
await connection.OpenAsync();
await using var command = new SlonCommand("SELECT 8", connection);

// await using var command = new SlonCommand("SELECT 8", dataSource);

await using var reader = await command.ExecuteReaderAsync();
await reader.ReadAsync();

Console.WriteLine(reader[0]);
