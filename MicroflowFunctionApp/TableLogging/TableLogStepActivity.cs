using System.Threading.Tasks;
using Microflow.Helpers;
using Microflow.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using static Microflow.Helpers.Constants;

namespace Microflow.TableLogging
{
    public static class TableLogStepActivity
    {
        [FunctionName(CallNames.LogStep)]
        public static async Task TableLogActivity([ActivityTrigger] LogStepEntity logEntity)
        {
            await logEntity.LogStep();
        }
    }
}