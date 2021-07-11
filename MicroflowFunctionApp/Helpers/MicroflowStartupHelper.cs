﻿using Microflow.Models;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Specialized;
using System.Threading.Tasks;

namespace Microflow.Helpers
{
    public static class MicroflowStartupHelper
    {
        /// <summary>
        /// Start a new project run
        /// </summary>
        /// <returns></returns>
        public static async Task StartMicroflowProject(this IDurableOrchestrationContext context, ILogger log, ProjectRun projectRun)
        {
            // log start
            string logRowKey = MicroflowTableHelper.GetTableLogRowKeyDescendingByDate(context.CurrentUtcDateTime, "_" + projectRun.OrchestratorInstanceId);

            LogOrchestrationEntity logEntity = await context.LogOrchestrationStartAsync(log, projectRun, logRowKey);

            await context.MicroflowStartProjectRun(log, projectRun);

            // log to table workflow completed
            context.LogOrchestrationEnd(projectRun, logRowKey, out logEntity, out Task logTask);

            context.SetProjectStateReady(projectRun);

            await logTask;
            // done
            log.LogError($"Project run {projectRun.ProjectName} completed successfully...");
            log.LogError("<<<<<<<<<<<<<<<<<<<<<<<<<-----> !!! A GREAT SUCCESS  !!! <----->>>>>>>>>>>>>>>>>>>>>>>>>");
        }

        /// <summary>
        /// Create a new ProjectRun for stratup, set GlobalKey
        /// </summary>
        public static ProjectRun CreateStartupProjectRun(NameValueCollection data, ref string instanceId, string projectName)
        {
            var input = new
            {
                Loop = Convert.ToInt32(data["loop"]),
                GlobalKey = data["globalkey"]
            };

            // create a project run
            ProjectRun projectRun = new ProjectRun()
            {
                ProjectName = projectName,
                Loop = input.Loop != 0
                ? input.Loop
                : 1
            };

            // create a new run object
            RunObject runObj = new RunObject() { StepNumber = "-1" };
            projectRun.RunObject = runObj;

            // instanceId is set/singleton
            if (!string.IsNullOrWhiteSpace(instanceId))
            {
                // globalKey is set
                if (!string.IsNullOrWhiteSpace(input.GlobalKey))
                {
                    runObj.GlobalKey = input.GlobalKey;
                }
                else
                {
                    runObj.GlobalKey = Guid.NewGuid().ToString();
                }
            }
            // instanceId is not set/multiple concurrent instances
            else
            {
                instanceId = Guid.NewGuid().ToString();
                // globalKey is set
                if (!string.IsNullOrWhiteSpace(input.GlobalKey))
                {
                    runObj.GlobalKey = input.GlobalKey;
                }
                else
                {
                    runObj.GlobalKey = instanceId;
                }
            }

            projectRun.RunObject.StepNumber = "-1";
            projectRun.OrchestratorInstanceId = instanceId;

            return projectRun;
        }

        public static void LogOrchestrationEnd(this IDurableOrchestrationContext context, ProjectRun projectRun, string logRowKey, out LogOrchestrationEntity logEntity, out Task logTask)
        {
            logEntity = new LogOrchestrationEntity(false,
                                                                   projectRun.ProjectName,
                                                                   logRowKey,
                                                                   $"{Environment.MachineName} - {projectRun.ProjectName} completed successfully",
                                                                   context.CurrentUtcDateTime,
                                                                   projectRun.OrchestratorInstanceId,
                                                                   projectRun.RunObject.GlobalKey);

            logTask = context.CallActivityAsync("LogOrchestration", logEntity);
        }

        public static async Task<LogOrchestrationEntity> LogOrchestrationStartAsync(this IDurableOrchestrationContext context, ILogger log, ProjectRun projectRun, string logRowKey)
        {
            LogOrchestrationEntity logEntity = new LogOrchestrationEntity(true,
                                                                       projectRun.ProjectName,
                                                                       logRowKey,
                                                                       $"{Environment.MachineName} - {projectRun.ProjectName} started...",
                                                                       context.CurrentUtcDateTime,
                                                                       projectRun.OrchestratorInstanceId,
                                                                       projectRun.RunObject.GlobalKey);

            await context.CallActivityAsync("LogOrchestration", logEntity);

            log.LogInformation($"Started orchestration with ID = '{context.InstanceId}', Project = '{projectRun.ProjectName}'");

            return logEntity;
        }
    }
}
