using System.Globalization;
using System.Reflection;

var assembly = Assembly.LoadFrom(args[0]);
try
{
    var result = assembly
        .GetType("Program", throwOnError: true)!
        .GetMethod(args[1], BindingFlags.Public | BindingFlags.Static)!
        .Invoke(null, null);
    Console.WriteLine(Convert.ToString(result, CultureInfo.InvariantCulture));
}
catch (TargetInvocationException error) when (error.InnerException is { } exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.WriteLine("THROW:" + exception.GetType().FullName);
}
