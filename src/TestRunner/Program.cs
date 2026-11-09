using System.Reflection;
using Xunit.Abstractions;
using Xunit.Runners;

// 进程内 xunit 运行器：绕开 testhost.x86 的 OpenProcess(父进程) 环境故障（Win32Exception(5)）。
// 用法：dotnet <TestRunner.dll> <测试程序集.dll> [类名或方法名包含过滤]
// 退出码：0=全绿，1=有失败/错误，2=参数错。

if (args.Length < 1)
{
    Console.Error.WriteLine("用法：TestRunner <测试程序集.dll> [名称包含过滤]");
    return 2;
}

var assemblyPath = Path.GetFullPath(args[0]);
if (!File.Exists(assemblyPath))
{
    Console.Error.WriteLine($"找不到测试程序集：{assemblyPath}");
    return 2;
}

var nameFilter = args.Length > 1 ? args[1] : "";
var assemblyName = AssemblyName.GetAssemblyName(assemblyPath).Name!;

// 测试程序集的依赖都在它自己的目录里，把它挂进解析路径
var testDir = Path.GetDirectoryName(assemblyPath)!;
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var file = Path.Combine(testDir, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(file) ? Assembly.LoadFrom(file) : null;
};

var passed = 0;
var failed = 0;
var skipped = 0;
var failures = new List<string>();
var failuresLock = new object();
var done = new ManualResetEventSlim(false);

var runner = AssemblyRunner.WithoutAppDomain(assemblyPath);
runner.OnDiscoveryComplete = info =>
    Console.WriteLine($"发现 {info.TestCasesToRun} 个测试（{assemblyName}）");

bool Match(string typeName, string methodName) =>
    nameFilter.Length == 0
    || typeName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)
    || methodName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase);

runner.OnTestPassed = info =>
{
    if (!Match(info.TypeName, info.MethodName)) return;
    Interlocked.Increment(ref passed);
    Console.WriteLine($"  通过 {info.TypeName}.{info.MethodName} ({info.ExecutionTime:0.###} ms)");
};

runner.OnTestFailed = info =>
{
    if (!Match(info.TypeName, info.MethodName)) return;
    Interlocked.Increment(ref failed);
    var name = $"{info.TypeName}.{info.MethodName}";
    Console.WriteLine($"  失败 {name}");
    Console.WriteLine($"    [{info.ExceptionType}] {info.ExceptionMessage}");
    if (!string.IsNullOrWhiteSpace(info.ExceptionStackTrace))
        Console.WriteLine($"    {info.ExceptionStackTrace.TrimEnd()}");
    if (!string.IsNullOrWhiteSpace(info.Output))
        Console.WriteLine("    ---- 测试输出 ----\n" + info.Output.TrimEnd());
    lock (failuresLock) failures.Add(name);
};

runner.OnTestSkipped = info =>
{
    if (!Match(info.TypeName, info.MethodName)) return;
    Interlocked.Increment(ref skipped);
    Console.WriteLine($"  跳过 {info.TypeName}.{info.MethodName}：{info.SkipReason}");
};

runner.OnErrorMessage = info =>
{
    Interlocked.Increment(ref failed);
    Console.WriteLine($"  错误 {info.MesssageType}");
    Console.WriteLine($"    [{info.ExceptionType}] {info.ExceptionMessage}");
    lock (failuresLock) failures.Add(info.MesssageType.ToString());
};

runner.OnDiagnosticMessage = info =>
{
    var msg = info.Message.TrimEnd();
    if (msg.Length > 0) Console.WriteLine($"  [诊断] {msg}");
};

runner.OnExecutionComplete = info =>
{
    Console.WriteLine($"== 摘要：通过 {passed}，失败 {failed}，跳过 {skipped}，"
                      + $"用时 {info.ExecutionTime:0.#} s ==");
    done.Set();
};

Console.WriteLine($"运行：{assemblyPath}");
runner.Start();
done.Wait();
return failed == 0 ? 0 : 1;
