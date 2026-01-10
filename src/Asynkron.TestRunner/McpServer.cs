using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asynkron.TestRunner;

/// <summary>
/// MCP (Model Context Protocol) server that proxies to the HTTP test runner server
/// </summary>
public class McpServer
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public McpServer(int port = 5123)
    {
        _baseUrl = $"http://localhost:{port}";
        _http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Read JSON-RPC messages from stdin, write responses to stdout
        using var reader = new StreamReader(Console.OpenStandardInput());
        using var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;

            try
            {
                var request = JsonNode.Parse(line);
                var response = await HandleRequestAsync(request);
                await writer.WriteLineAsync(response?.ToJsonString());
            }
            catch (Exception ex)
            {
                var error = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["error"] = new JsonObject
                    {
                        ["code"] = -32603,
                        ["message"] = ex.Message
                    }
                };
                await writer.WriteLineAsync(error.ToJsonString());
            }
        }
    }

    private async Task<JsonNode?> HandleRequestAsync(JsonNode? request)
    {
        if (request == null) return null;

        var method = request["method"]?.GetValue<string>();
        var id = request["id"];
        var @params = request["params"];

        JsonNode? result = method switch
        {
            "initialize" => HandleInitialize(),
            "tools/list" => HandleToolsList(),
            "tools/call" => await HandleToolCallAsync(@params),
            _ => null
        };

        if (result == null && method?.StartsWith("notifications/") != true)
        {
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["error"] = new JsonObject
                {
                    ["code"] = -32601,
                    ["message"] = $"Unknown method: {method}"
                }
            };
        }

        if (result == null) return null;

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result
        };
    }

    private JsonNode HandleInitialize()
    {
        return new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject()
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "testrunner",
                ["version"] = "1.0.0"
            }
        };
    }

    private JsonNode HandleToolsList()
    {
        return new JsonObject
        {
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "discover_tests",
                    ["description"] = "Discover tests in a .NET test assembly",
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["assembly"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Path to the test assembly (.dll)"
                            },
                            ["filter"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Optional filter pattern (e.g., 'Class=Foo', 'Method=Bar')"
                            }
                        },
                        ["required"] = new JsonArray { "assembly" }
                    }
                },
                new JsonObject
                {
                    ["name"] = "run_tests",
                    ["description"] = "Run tests in a .NET test assembly. The test runner UI will be shown in the server terminal.",
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["assembly"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Path to the test assembly (.dll)"
                            },
                            ["filter"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Optional filter pattern"
                            },
                            ["timeout"] = new JsonObject
                            {
                                ["type"] = "integer",
                                ["description"] = "Per-test timeout in seconds (default: 30)"
                            },
                            ["workers"] = new JsonObject
                            {
                                ["type"] = "integer",
                                ["description"] = "Number of parallel workers (default: 1)"
                            }
                        },
                        ["required"] = new JsonArray { "assembly" }
                    }
                },
                new JsonObject
                {
                    ["name"] = "get_status",
                    ["description"] = "Get the status of the current or last test run",
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject()
                    }
                },
                new JsonObject
                {
                    ["name"] = "cancel_run",
                    ["description"] = "Cancel the current test run",
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject()
                    }
                },
                new JsonObject
                {
                    ["name"] = "get_test_result",
                    ["description"] = "Get detailed result for a specific test by name pattern. Returns output, error messages, and stack traces.",
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["pattern"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Pattern to match test name (case-insensitive contains)"
                            }
                        },
                        ["required"] = new JsonArray { "pattern" }
                    }
                },
                new JsonObject
                {
                    ["name"] = "list_tests",
                    ["description"] = "List tests from the last run, optionally filtered by status and/or name pattern. Use this to discover test names before querying details.",
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["status"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Filter by status: all, passed, failed, crashed, hanging, skipped (default: all)",
                                ["enum"] = new JsonArray { "all", "passed", "failed", "crashed", "hanging", "skipped" }
                            },
                            ["pattern"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Optional pattern to filter test names (case-insensitive contains)"
                            }
                        }
                    }
                }
            }
        };
    }

    private async Task<JsonNode?> HandleToolCallAsync(JsonNode? @params)
    {
        var toolName = @params?["name"]?.GetValue<string>();
        var args = @params?["arguments"];

        try
        {
            var result = toolName switch
            {
                "discover_tests" => await CallDiscoverAsync(args),
                "run_tests" => await CallRunAsync(args),
                "get_status" => await CallStatusAsync(),
                "cancel_run" => await CallCancelAsync(),
                "get_test_result" => await CallGetResultAsync(args),
                "list_tests" => await CallListAsync(args),
                _ => throw new Exception($"Unknown tool: {toolName}")
            };

            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = result
                    }
                }
            };
        }
        catch (HttpRequestException ex)
        {
            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = $"Error: Could not connect to test runner server at {_baseUrl}. Make sure 'testrunner serve' is running.\n\nDetails: {ex.Message}"
                    }
                },
                ["isError"] = true
            };
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = $"Error: {ex.Message}"
                    }
                },
                ["isError"] = true
            };
        }
    }

    private async Task<string> CallDiscoverAsync(JsonNode? args)
    {
        var body = new JsonObject
        {
            ["Assembly"] = args?["assembly"]?.GetValue<string>(),
            ["Filter"] = args?["filter"]?.GetValue<string>()
        };

        var response = await _http.PostAsync("/discover",
            new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));

        var result = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(result);

        if (json?["error"] != null)
            throw new Exception(json["error"]!.GetValue<string>());

        var count = json?["count"]?.GetValue<int>() ?? 0;
        var tests = json?["tests"]?.AsArray();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Found {count} tests:");
        if (tests != null)
        {
            foreach (var test in tests.Take(50))
            {
                sb.AppendLine($"  - {test?["FullyQualifiedName"]?.GetValue<string>()}");
            }
            if (count > 50)
                sb.AppendLine($"  ... and {count - 50} more");
        }

        return sb.ToString();
    }

    private async Task<string> CallRunAsync(JsonNode? args)
    {
        var body = new JsonObject
        {
            ["Assembly"] = args?["assembly"]?.GetValue<string>(),
            ["Filter"] = args?["filter"]?.GetValue<string>(),
            ["Timeout"] = args?["timeout"]?.GetValue<int>(),
            ["Workers"] = args?["workers"]?.GetValue<int>()
        };

        var response = await _http.PostAsync("/run",
            new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));

        var result = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(result);

        if (json?["error"] != null)
            throw new Exception(json["error"]!.GetValue<string>());

        var runId = json?["runId"]?.GetValue<string>();
        return $"Test run started (ID: {runId}). Use get_status to check progress, or watch the server terminal for live UI.";
    }

    private async Task<string> CallStatusAsync()
    {
        var response = await _http.GetAsync("/status");
        var result = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(result);

        var state = json?["state"]?.GetValue<string>() ?? "unknown";

        if (state == "idle")
            return "No test run in progress.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"State: {state}");

        if (json?["assembly"] != null)
            sb.AppendLine($"Assembly: {json["assembly"]!.GetValue<string>()}");

        var passed = json?["passed"]?.GetValue<int>() ?? 0;
        var failed = json?["failed"]?.GetValue<int>() ?? 0;
        var skipped = json?["skipped"]?.GetValue<int>() ?? 0;
        var crashed = json?["crashed"]?.GetValue<int>() ?? 0;
        var hanging = json?["hanging"]?.GetValue<int>() ?? 0;
        var total = passed + failed + skipped + crashed + hanging;

        if (state == "running")
        {
            sb.AppendLine($"Progress: {total} tests completed");
            sb.AppendLine($"  ✓ Passed: {passed}");
            if (failed > 0) sb.AppendLine($"  ✗ Failed: {failed}");
            if (crashed > 0) sb.AppendLine($"  💥 Crashed: {crashed}");
            if (hanging > 0) sb.AppendLine($"  ⏱ Hanging: {hanging}");
            if (skipped > 0) sb.AppendLine($"  ⊘ Skipped: {skipped}");
            sb.AppendLine();
            sb.AppendLine("Tests are running. Watch the server terminal for live progress.");
        }
        else if (state == "passed" || state == "failed")
        {
            sb.AppendLine();
            sb.AppendLine($"Summary ({total} tests):");
            sb.AppendLine($"  ✓ Passed:  {passed}");
            sb.AppendLine($"  ✗ Failed:  {failed}");
            sb.AppendLine($"  💥 Crashed: {crashed}");
            sb.AppendLine($"  ⏱ Hanging: {hanging}");
            sb.AppendLine($"  ⊘ Skipped: {skipped}");

            // Show problematic tests
            void ShowTests(string label, string icon, JsonArray? tests)
            {
                if (tests != null && tests.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"{label}:");
                    foreach (var test in tests.Take(10))
                    {
                        sb.AppendLine($"  {icon} {test?.GetValue<string>()}");
                    }
                    if (tests.Count > 10)
                        sb.AppendLine($"  ... and {tests.Count - 10} more");
                }
            }

            ShowTests("Failed tests", "✗", json?["failedTests"]?.AsArray());
            ShowTests("Crashed tests", "💥", json?["crashedTests"]?.AsArray());
            ShowTests("Hanging tests", "⏱", json?["hangingTests"]?.AsArray());

            sb.AppendLine();
            sb.AppendLine("Use list_tests to see all tests, or get_test_result for details on specific tests.");
        }
        else if (state == "error")
        {
            sb.AppendLine($"Error: {json?["error"]?.GetValue<string>()}");
        }

        return sb.ToString();
    }

    private async Task<string> CallCancelAsync()
    {
        var response = await _http.PostAsync("/cancel", null);
        await response.Content.ReadAsStringAsync();
        return "Cancel requested. The current test run will stop.";
    }

    private async Task<string> CallGetResultAsync(JsonNode? args)
    {
        var pattern = args?["pattern"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(pattern))
            throw new Exception("Pattern is required");

        var response = await _http.GetAsync($"/result?pattern={Uri.EscapeDataString(pattern)}");
        var result = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(result);

        if (json?["error"] != null)
            return $"Error: {json["error"]!.GetValue<string>()}" +
                   (json["totalTests"] != null ? $" (total tests in results: {json["totalTests"]!.GetValue<int>()})" : "");

        var count = json?["count"]?.GetValue<int>() ?? 0;
        var results = json?["results"]?.AsArray();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Found {count} test(s) matching '{pattern}':");
        sb.AppendLine();

        if (results != null)
        {
            foreach (var test in results)
            {
                var fqn = test?["fullyQualifiedName"]?.GetValue<string>() ?? "Unknown";
                var status = test?["status"]?.GetValue<string>() ?? "unknown";
                var durationMs = test?["durationMs"]?.GetValue<double>() ?? 0;
                var errorMessage = test?["errorMessage"]?.GetValue<string>();
                var stackTrace = test?["stackTrace"]?.GetValue<string>();
                var output = test?["output"]?.GetValue<string>();
                var skipReason = test?["skipReason"]?.GetValue<string>();

                sb.AppendLine($"=== {fqn} ===");
                sb.AppendLine($"Status: {status}");
                sb.AppendLine($"Duration: {durationMs:F1}ms");

                if (!string.IsNullOrEmpty(skipReason))
                    sb.AppendLine($"Skip reason: {skipReason}");

                if (!string.IsNullOrEmpty(errorMessage))
                {
                    sb.AppendLine($"Error: {errorMessage}");
                }

                if (!string.IsNullOrEmpty(stackTrace))
                {
                    sb.AppendLine("Stack trace:");
                    sb.AppendLine(stackTrace);
                }

                if (!string.IsNullOrEmpty(output))
                {
                    sb.AppendLine("Output:");
                    sb.AppendLine(output);
                }

                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private async Task<string> CallListAsync(JsonNode? args)
    {
        var status = args?["status"]?.GetValue<string>() ?? "all";
        var pattern = args?["pattern"]?.GetValue<string>();

        var queryString = $"?status={Uri.EscapeDataString(status)}";
        if (!string.IsNullOrWhiteSpace(pattern))
            queryString += $"&pattern={Uri.EscapeDataString(pattern)}";

        var response = await _http.GetAsync($"/list{queryString}");
        var result = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(result);

        var count = json?["count"]?.GetValue<int>() ?? 0;
        var totalTests = json?["totalTests"]?.GetValue<int>() ?? 0;
        var tests = json?["tests"]?.AsArray();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Found {count} tests (of {totalTests} total)");
        if (status != "all")
            sb.AppendLine($"Filter: status={status}");
        if (!string.IsNullOrWhiteSpace(pattern))
            sb.AppendLine($"Filter: pattern={pattern}");
        sb.AppendLine();

        if (tests != null && tests.Count > 0)
        {
            // Group by status for readability
            var grouped = tests
                .GroupBy(t => t?["status"]?.GetValue<string>() ?? "unknown")
                .OrderBy(g => g.Key switch
                {
                    "failed" => 0,
                    "crashed" => 1,
                    "hanging" => 2,
                    "passed" => 3,
                    "skipped" => 4,
                    _ => 5
                });

            foreach (var group in grouped)
            {
                var icon = group.Key switch
                {
                    "passed" => "✓",
                    "failed" => "✗",
                    "crashed" => "💥",
                    "hanging" => "⏱",
                    "skipped" => "⊘",
                    _ => "?"
                };
                sb.AppendLine($"[{group.Key.ToUpperInvariant()}] ({group.Count()})");
                foreach (var test in group.Take(50))
                {
                    var name = test?["name"]?.GetValue<string>() ?? "?";
                    var hasError = test?["hasError"]?.GetValue<bool>() ?? false;
                    var hasOutput = test?["hasOutput"]?.GetValue<bool>() ?? false;
                    var annotations = new List<string>();
                    if (hasError) annotations.Add("has error");
                    if (hasOutput) annotations.Add("has output");
                    var suffix = annotations.Count > 0 ? $" ({string.Join(", ", annotations)})" : "";
                    sb.AppendLine($"  {icon} {name}{suffix}");
                }
                if (group.Count() > 50)
                    sb.AppendLine($"  ... and {group.Count() - 50} more");
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("No tests match the criteria.");
        }

        return sb.ToString();
    }
}
