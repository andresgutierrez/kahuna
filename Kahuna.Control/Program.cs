
/**
 * This file is part of Kahuna
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CommandLine;
using DotNext.Threading.Tasks;
using RadLine;
using Spectre.Console;
using Kahuna.Client;
using Kahuna.Shared.KeyValue;
using Kommander.Communication.Rest;
using Microsoft.Extensions.Logging;

ParserResult<Options> optsResult = Parser.Default.ParseArguments<Options>(args);

Options? opts = optsResult.Value;
if (opts is null)
    return;

Console.WriteLine("Kahuna Shell 0.0.1 (alpha)\n");

const int DefaultExpires = 5 * 86400 * 365;

string historyPath = Path.GetTempPath() + Path.PathSeparator + "kahuna.history.json";
List<string> history = await GetHistory(historyPath);

KahunaClient connection = await GetConnection(opts);

LineEditor? editor = null;
Dictionary<string, KahunaLock> locks = new();

if (LineEditor.IsSupported(AnsiConsole.Console))
{
    string[] keywords =
    [
        // ephemeral key/values
        "eset",
        "eget",
        "edel",
        "edelete",
        "eextend",
        // linearizable key/values
        "set",
        "get",
        "del",
        "delete",
        "extend",
        "nx",
        "xx",
        // locks
        "lock",
        "extend-lock",
        "unlock",
        "get-lock",
    ];

    string[] functions = [];

    string[] commands =
    [
        "clear",
        "exit",
        "quit"
    ];

    WordHighlighter worldHighlighter = new();

    Style funcStyle = new(foreground: Color.Aqua);
    Style keywordStyle = new(foreground: Color.Blue);
    Style commandStyle = new(foreground: Color.LightSkyBlue1);

    foreach (string keyword in keywords)
        worldHighlighter.AddWord(keyword, keywordStyle);

    foreach (string func in functions)
        worldHighlighter.AddWord(func, funcStyle);

    foreach (string command in commands)
        worldHighlighter.AddWord(command, commandStyle);

    editor = new()
    {
        MultiLine = false,
        Text = "",
        Prompt = new MyLineNumberPrompt(new(foreground: Color.PaleTurquoise1)),
        //Completion = new TestCompletion(),        
        Highlighter = worldHighlighter
    };

    foreach (string item in history)
        editor.History.Add(item);
}

Console.CancelKeyPress += delegate
{
    AnsiConsole.MarkupLine("[cyan]\nExiting...[/]");

    foreach (KeyValuePair<string, KahunaLock> kvp in locks)
    {
        AnsiConsole.MarkupLine("[yellow]Disposing lock {0}...[/]", kvp.Value.Owner);

        kvp.Value.DisposeAsync().Wait();
    }
    
    SaveHistory(historyPath, history).Wait();
};

while (true)
{
    try
    {
        string? command;

        if (editor is not null)
            command = await editor.ReadLine(CancellationToken.None);
        else
            command = AnsiConsole.Prompt(new TextPrompt<string>("kahuna-cli> ").AllowEmpty());

        if (string.IsNullOrWhiteSpace(command))
            continue;

        string commandTrim = command.Trim();
        
        if (string.Equals(commandTrim, "exit", StringComparison.InvariantCultureIgnoreCase) || string.Equals(commandTrim, "quit", StringComparison.InvariantCultureIgnoreCase))
        {
            foreach (KeyValuePair<string, KahunaLock> kvp in locks)
            {
                try
                {
                    AnsiConsole.MarkupLine("[yellow]Disposing lock {0}...[/]", kvp.Value.Owner);

                    await kvp.Value.DisposeAsync();
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]{0}[/]: {1}\n", Markup.Escape(ex.GetType().Name), Markup.Escape(ex.Message));
                }
            }

            await SaveHistory(historyPath, history);
            break;
        }
        
        if (string.Equals(commandTrim, "clear", StringComparison.InvariantCultureIgnoreCase))
        {
            AnsiConsole.Clear();
            continue;
        }

        if (commandTrim.StartsWith("set ", StringComparison.InvariantCultureIgnoreCase))
        {
            history.Add(commandTrim);
            
            await SetKey(commandTrim, KeyValueConsistency.Linearizable);
            continue;
        }
        
        if (commandTrim.StartsWith("get ", StringComparison.InvariantCultureIgnoreCase))
        {
            history.Add(commandTrim);
            
            await GetKey(commandTrim, KeyValueConsistency.Linearizable);
            continue;
        }
        
        if (commandTrim.StartsWith("delete ", StringComparison.InvariantCultureIgnoreCase) || commandTrim.StartsWith("del ", StringComparison.InvariantCultureIgnoreCase))
        {
            history.Add(commandTrim);
            
            await DeleteKey(commandTrim, KeyValueConsistency.Linearizable);
            continue;
        }
        
        if (commandTrim.StartsWith("extend ", StringComparison.InvariantCultureIgnoreCase))
        {
            history.Add(commandTrim);
            
            await ExtendKey(commandTrim, KeyValueConsistency.Linearizable);
            continue;
        }
        
        if (commandTrim.StartsWith("eset ", StringComparison.InvariantCultureIgnoreCase))
        {
            history.Add(commandTrim);
            
            await SetKey(commandTrim, KeyValueConsistency.Ephemeral);
            continue;
        }
        
        if (commandTrim.StartsWith("eget ", StringComparison.InvariantCultureIgnoreCase))
        {
            history.Add(commandTrim);
            
            await GetKey(commandTrim, KeyValueConsistency.Ephemeral);
            continue;
        }
        
        if (commandTrim.StartsWith("edelete ", StringComparison.InvariantCultureIgnoreCase) || commandTrim.StartsWith("edel ", StringComparison.InvariantCultureIgnoreCase))
        {
            history.Add(commandTrim);
            
            await DeleteKey(commandTrim, KeyValueConsistency.Ephemeral);
            continue;
        }
        
        if (commandTrim.StartsWith("lock ", StringComparison.InvariantCultureIgnoreCase))
        {
            history.Add(commandTrim);
            
            string[] parts = commandTrim.Split(" ", StringSplitOptions.RemoveEmptyEntries);

            KahunaLock kahunaLock = await connection.GetOrCreateLock(parts[1], int.Parse(parts[2]));

            if (kahunaLock.IsAcquired)
            {
                AnsiConsole.MarkupLine("[cyan]acquired {0} rev:{1}[/]\n", Markup.Escape(Encoding.UTF8.GetString(kahunaLock.Owner)), Markup.Escape(kahunaLock.FencingToken.ToString()));
                
                locks.TryAdd(parts[1], kahunaLock);
            }
            else
                AnsiConsole.MarkupLine("[yellow]not acquired[/]\n");

            continue;
        }
        
        if (commandTrim.StartsWith("unlock ", StringComparison.InvariantCultureIgnoreCase))
        {
            history.Add(commandTrim);
            
            string[] parts = commandTrim.Split(" ", StringSplitOptions.RemoveEmptyEntries);

            if (locks.TryGetValue(parts[1], out KahunaLock? kahunaLock))
            {
                await kahunaLock.DisposeAsync();

                //if (success)
                    AnsiConsole.MarkupLine("[cyan]unlocked[/]");
                //else
                //    AnsiConsole.MarkupLine("[yellow]not unlocked[/]");
                
                locks.Remove(parts[1]);
            } 
            else
            {
                AnsiConsole.MarkupLine("[yellow]not acquired[/]");
            }
            
            continue;
        }
        
        if (commandTrim.StartsWith("get-lock ", StringComparison.InvariantCultureIgnoreCase))
        {
            history.Add(commandTrim);
            
            string[] parts = commandTrim.Split(" ", StringSplitOptions.RemoveEmptyEntries);

            if (locks.TryGetValue(parts[1], out KahunaLock? kahunaLock))
            {
                bool success = await connection.Unlock(parts[1], kahunaLock.Owner);

                if (success)
                    AnsiConsole.MarkupLine("[cyan]got {0} rev:{1}[/]", Markup.Escape(Encoding.UTF8.GetString(kahunaLock.Owner)), kahunaLock.FencingToken);
                else
                    AnsiConsole.MarkupLine("[yellow]not acquired[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]not acquired[/]");
            }
            
            continue;
        }
        
        if (commandTrim.StartsWith("extend-lock ", StringComparison.InvariantCultureIgnoreCase))
        {
            history.Add(commandTrim);
            
            string[] parts = commandTrim.Split(" ", StringSplitOptions.RemoveEmptyEntries);

            if (locks.TryGetValue(parts[1], out KahunaLock? kahunaLock))
            {
                (bool success, long fencingToken) = await kahunaLock.TryExtend(int.Parse(parts[2]));

                if (success)
                    AnsiConsole.MarkupLine("[cyan]got {0} rev:{1}[/]", Markup.Escape(Encoding.UTF8.GetString(kahunaLock.Owner)), fencingToken);
                else
                    AnsiConsole.MarkupLine("[yellow]not acquired[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]not acquired[/]");
            }
            
            continue;
        }
        
        AnsiConsole.MarkupLine("[yellow]unknown command[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine("[red]{0}[/]: {1}\n", Markup.Escape(ex.GetType().Name), Markup.Escape(ex.Message));
    }
}


static async Task<KahunaClient> GetConnection(Options opts)
{
    string? connectionString = opts.ConnectionSource;

    if (string.IsNullOrEmpty(connectionString))
        connectionString = "https://localhost:8082";
    
    await Task.CompletedTask;

    return new(connectionString, null, new Kahuna.Client.Communication.RestCommunication(null));
}

static async Task<List<string>> GetHistory(string historyPath)
{
    List<string>? history = [];

    if (File.Exists(historyPath))
    {
        try
        {
            string historyText = await File.ReadAllTextAsync(historyPath);
            history = JsonSerializer.Deserialize<List<string>>(historyText);
        }
        catch
        {
            AnsiConsole.MarkupLine("[yellow]Found invalid history[/]");
        }
    }

    history ??= [];

    return history;
}

static async Task SaveHistory(string historyPath, List<string>? history)
{
    if (history is not null)
        await File.WriteAllTextAsync(historyPath, JsonSerializer.Serialize(history));
}

async Task SetKey(string commandTrim, KeyValueConsistency consistency)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
            
    string[] parts = commandTrim.Split(" ", StringSplitOptions.RemoveEmptyEntries);

    int expires = DefaultExpires;
    KeyValueFlags flags = KeyValueFlags.Set;

    if (parts.Length >= 4)
    {
        if (parts[3].Equals("NX", StringComparison.InvariantCultureIgnoreCase))
            flags = KeyValueFlags.SetIfNotExists;
        else if (parts[3].Equals("XX", StringComparison.InvariantCultureIgnoreCase))
            flags = KeyValueFlags.SetIfExists;
        else
            expires = int.Parse(parts[3]);
    }

    (bool success, long revision) = await connection.SetKeyValue(parts[1], Encoding.UTF8.GetBytes(parts[2]), expires, flags, consistency);
            
    if (success)
        AnsiConsole.MarkupLine("r{0} [cyan]ok[/] {1}ms\n", revision, stopwatch.ElapsedMilliseconds);
    else
        AnsiConsole.MarkupLine("r{0} [yellow](not set)[/] {1}ms\n", revision, stopwatch.ElapsedMilliseconds);
}

async Task GetKey(string commandTrim, KeyValueConsistency consistency)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
            
    string[] parts = commandTrim.Split(" ", StringSplitOptions.RemoveEmptyEntries);

    (byte[]? value, long revision) = await connection.GetKeyValue(parts[1], consistency);
            
    if (value is not null)
        AnsiConsole.MarkupLine("r{0} [cyan]{1}[/] {2}ms\n", revision, Markup.Escape(Encoding.UTF8.GetString(value)), stopwatch.ElapsedMilliseconds);
    else
        AnsiConsole.MarkupLine("r{0} [yellow]null[/] {1}ms\n", revision, stopwatch.ElapsedMilliseconds);
}

async Task DeleteKey(string commandTrim, KeyValueConsistency consistency)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    
    string[] parts = commandTrim.Split(" ", StringSplitOptions.RemoveEmptyEntries);

    (bool success, long revision) = await connection.DeleteKeyValue(parts[1], consistency);
            
    if (success)
        AnsiConsole.MarkupLine("r{0} [cyan]deleted[/] {1}ms\n", revision, stopwatch.ElapsedMilliseconds);
    else
        AnsiConsole.MarkupLine("r{0} [yellow]not found[/] {1}ms\n", revision, stopwatch.ElapsedMilliseconds);
}

async Task ExtendKey(string commandTrim, KeyValueConsistency consistency)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    
    string[] parts = commandTrim.Split(" ", StringSplitOptions.RemoveEmptyEntries);

    (bool success, long revision) = await connection.ExtendKeyValue(parts[1], int.Parse(parts[2]), consistency);
            
    if (success)
        AnsiConsole.MarkupLine("r{0} [cyan]extended[/] {1}ms\n", revision, stopwatch.ElapsedMilliseconds);
    else
        AnsiConsole.MarkupLine("r{0} [yellow]not found[/] {1}ms\n", revision, stopwatch.ElapsedMilliseconds);
}

public sealed class MyLineNumberPrompt : ILineEditorPrompt
{
    private readonly Style _style;

    public MyLineNumberPrompt(Style? style = null)
    {
        _style = style ?? new Style(foreground: Color.Yellow, background: Color.Blue);
    }

    public (Markup Markup, int Margin) GetPrompt(ILineEditorState state, int line)
    {
        return (new Markup("kahuna-cli> ", _style), 1);
    }
}

public sealed class Options
{
    [Option('c', "connection-source", Required = false, HelpText = "Set the connection string")]
    public string? ConnectionSource { get; set; }
}