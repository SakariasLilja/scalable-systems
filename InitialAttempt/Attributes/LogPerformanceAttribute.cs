using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.Filters;

namespace InitialAttempt.Attributes;

public class LogPerformanceAttribute : ActionFilterAttribute
{
    private const string StopwatchKey = "Stopwatch";

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        context.HttpContext.Items[StopwatchKey] = Stopwatch.StartNew();
    }

    public override void OnActionExecuted(ActionExecutedContext context)
    {
        if (context.HttpContext.Items.TryGetValue(StopwatchKey, out var stopwatchObj) && stopwatchObj is Stopwatch stopwatch)
        {
            stopwatch.Stop();

            var logger = context.HttpContext.RequestServices.GetService<ILogger<LogPerformanceAttribute>>();
            logger?.LogInformation("Action {ActionName} executed in {ElapsedMilliseconds} ms", context.ActionDescriptor.DisplayName, stopwatch.ElapsedMilliseconds);
        }
    }
}