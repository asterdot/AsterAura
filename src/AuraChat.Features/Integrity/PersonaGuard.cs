using System.Text.Json.Nodes;
using AuraChat.Core.Ai;
using AuraChat.Core.Logging;
using AuraChat.Core.Storage;

namespace AuraChat.Features.Integrity;

/// <summary>
/// 人格保护器：确保"记忆≠人格"的最高原则。
/// 通过固定问题集进行人格回归测试，检测人格是否被记忆系统改写。
/// </summary>
public sealed class PersonaGuard
{
    private readonly DataStore _store;
    private readonly AreaLog _log;
    private readonly string _baselineFile;

    public PersonaGuard(DataStore store, AreaLog log)
    {
        _store = store;
        _log = log;
        
        // 决策：放在 data/integrity/ 目录
        var integrityDir = Path.Combine(store.DataDir, "integrity");
        if (!Directory.Exists(integrityDir))
            Directory.CreateDirectory(integrityDir);
        
        _baselineFile = Path.Combine(integrityDir, "persona.baseline.json");
    }

    /// <summary>
    /// 初始化人格基线：创建测试问题集
    /// </summary>
    public void InitializeBaseline(string personaId, List<PersonaTestQuestion> questions)
    {
        var baseline = new JsonObject
        {
            ["version"] = "1.0",
            ["personaId"] = personaId,
            ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["lastTest"] = 0L,
            ["baselineQuestions"] = SerializeQuestions(questions),
            ["testResults"] = new JsonArray()
        };

        DataStore.WriteJson(_baselineFile, baseline);
        _log.Info($"初始化人格基线：{personaId}，{questions.Count} 个测试问题");
    }

    /// <summary>
    /// 执行人格回归测试
    /// </summary>
    public async Task<PersonaTestResult> RunRegressionTestAsync(
        ILlmClient llm,
        Func<List<PersonaTestQuestion>, Task<List<PersonaTestAnswer>>> answerFunc,
        CancellationToken ct = default)
    {
        var baseline = LoadBaseline();
        if (baseline is null)
        {
            return new PersonaTestResult
            {
                Success = false,
                Message = "人格基线未初始化"
            };
        }

        var questions = DeserializeQuestions(baseline["baselineQuestions"] as JsonArray);
        if (questions.Count == 0)
        {
            return new PersonaTestResult
            {
                Success = false,
                Message = "基线问题集为空"
            };
        }

        _log.Info($"开始人格回归测试：{questions.Count} 个问题");

        // 获取回答
        List<PersonaTestAnswer> answers;
        try
        {
            answers = await answerFunc(questions);
        }
        catch (Exception ex)
        {
            _log.Warn("获取测试回答失败：", ex.Message);
            return new PersonaTestResult
            {
                Success = false,
                Message = $"测试执行失败：{ex.Message}"
            };
        }

        // 验证回答
        var result = new PersonaTestResult
        {
            Success = true,
            TotalQuestions = questions.Count,
            TestAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var violations = new List<string>();

        for (var i = 0; i < questions.Count && i < answers.Count; i++)
        {
            var question = questions[i];
            var answer = answers[i];

            var passed = true;
            var issues = new List<string>();

            // 检查必须包含的关键词
            if (question.ExpectedKeywords.Count > 0)
            {
                var matchedKeywords = question.ExpectedKeywords
                    .Count(kw => answer.Answer.Contains(kw, StringComparison.OrdinalIgnoreCase));

                if (matchedKeywords == 0)
                {
                    passed = false;
                    issues.Add($"未包含任何期望关键词：{string.Join(", ", question.ExpectedKeywords)}");
                }
            }

            // 检查禁止包含的内容
            if (question.MustNotContain.Count > 0)
            {
                foreach (var forbidden in question.MustNotContain)
                {
                    if (answer.Answer.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                    {
                        passed = false;
                        issues.Add($"包含禁止内容：{forbidden}");
                    }
                }
            }

            if (!passed)
            {
                result.FailedQuestions++;
                violations.Add($"问题 {question.Id}: {string.Join("; ", issues)}");
            }
            else
            {
                result.PassedQuestions++;
            }
        }

        // 判断是否通过
        var passRate = (double)result.PassedQuestions / result.TotalQuestions;
        result.Success = passRate >= 0.8; // 80% 通过率

        if (!result.Success)
        {
            result.Message = $"人格回归测试失败（通过率 {passRate:P0}）：\n" + string.Join("\n", violations);
            _log.Warn(result.Message);
        }
        else
        {
            result.Message = $"人格回归测试通过（{result.PassedQuestions}/{result.TotalQuestions}）";
            _log.Info(result.Message);
        }

        // 记录测试结果
        RecordTestResult(baseline, result);

        return result;
    }

    /// <summary>
    /// 获取测试历史
    /// </summary>
    public List<PersonaTestResult> GetTestHistory(int limit = 10)
    {
        var baseline = LoadBaseline();
        if (baseline is null || baseline["testResults"] is not JsonArray results)
            return new List<PersonaTestResult>();

        var history = new List<PersonaTestResult>();
        foreach (var node in results.TakeLast(limit))
        {
            if (node is not JsonObject obj)
                continue;

            history.Add(new PersonaTestResult
            {
                Success = obj["success"]?.GetValue<bool>() ?? false,
                TestAt = obj["testAt"]?.GetValue<long>() ?? 0,
                TotalQuestions = obj["totalQuestions"]?.GetValue<int>() ?? 0,
                PassedQuestions = obj["passedQuestions"]?.GetValue<int>() ?? 0,
                FailedQuestions = obj["failedQuestions"]?.GetValue<int>() ?? 0,
                Message = obj["message"]?.ToString() ?? ""
            });
        }

        return history;
    }

    // ========== Private Methods ==========

    private JsonObject? LoadBaseline()
    {
        if (!File.Exists(_baselineFile))
            return null;

        return DataStore.ReadObject(_baselineFile);
    }

    private JsonArray SerializeQuestions(List<PersonaTestQuestion> questions)
    {
        var arr = new JsonArray();
        foreach (var q in questions)
        {
            var obj = new JsonObject
            {
                ["id"] = q.Id,
                ["question"] = q.Question,
                ["expectedKeywords"] = new JsonArray(q.ExpectedKeywords.Select(k => JsonValue.Create(k)).ToArray()),
                ["mustNotContain"] = new JsonArray(q.MustNotContain.Select(k => JsonValue.Create(k)).ToArray())
            };
            arr.Add(obj);
        }
        return arr;
    }

    private List<PersonaTestQuestion> DeserializeQuestions(JsonArray? arr)
    {
        var questions = new List<PersonaTestQuestion>();
        if (arr is null)
            return questions;

        foreach (var node in arr)
        {
            if (node is not JsonObject obj)
                continue;

            var question = new PersonaTestQuestion
            {
                Id = obj["id"]?.ToString() ?? "",
                Question = obj["question"]?.ToString() ?? ""
            };

            if (obj["expectedKeywords"] is JsonArray expArr)
            {
                foreach (var kw in expArr)
                {
                    var str = kw?.ToString();
                    if (!string.IsNullOrEmpty(str))
                        question.ExpectedKeywords.Add(str);
                }
            }

            if (obj["mustNotContain"] is JsonArray forbArr)
            {
                foreach (var kw in forbArr)
                {
                    var str = kw?.ToString();
                    if (!string.IsNullOrEmpty(str))
                        question.MustNotContain.Add(str);
                }
            }

            questions.Add(question);
        }

        return questions;
    }

    private void RecordTestResult(JsonObject baseline, PersonaTestResult result)
    {
        baseline["lastTest"] = result.TestAt;

        var results = baseline["testResults"] as JsonArray ?? new JsonArray();
        baseline["testResults"] = results;

        results.Add(new JsonObject
        {
            ["success"] = result.Success,
            ["testAt"] = result.TestAt,
            ["totalQuestions"] = result.TotalQuestions,
            ["passedQuestions"] = result.PassedQuestions,
            ["failedQuestions"] = result.FailedQuestions,
            ["message"] = result.Message
        });

        // 只保留最近20次结果
        while (results.Count > 20)
            results.RemoveAt(0);

        DataStore.WriteJson(_baselineFile, baseline);
    }
}

/// <summary>人格测试问题</summary>
public sealed class PersonaTestQuestion
{
    public string Id { get; set; } = "";
    public string Question { get; set; } = "";
    public List<string> ExpectedKeywords { get; set; } = new();
    public List<string> MustNotContain { get; set; } = new();
}

/// <summary>人格测试回答</summary>
public sealed class PersonaTestAnswer
{
    public string QuestionId { get; set; } = "";
    public string Answer { get; set; } = "";
}

/// <summary>人格测试结果</summary>
public sealed class PersonaTestResult
{
    public bool Success { get; set; }
    public long TestAt { get; set; }
    public int TotalQuestions { get; set; }
    public int PassedQuestions { get; set; }
    public int FailedQuestions { get; set; }
    public string Message { get; set; } = "";
}
