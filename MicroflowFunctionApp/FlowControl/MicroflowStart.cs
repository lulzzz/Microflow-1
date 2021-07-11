using System;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using System.Text.Json;
using Microflow.Helpers;
using MicroflowModels;
using System.Net;
using Microflow.Models;
using Microsoft.Azure.Cosmos.Table;
using System.Threading;
using System.Collections.Specialized;

namespace Microflow.FlowControl
{
    /// <summary>
    /// "Microflow_InsertOrUpdateProject" must be called to save project step meta data to table storage
    /// after this, "Microflow_HttpStart" can be called multiple times,
    /// if a change is made to the project, call "Microflow_InsertOrUpdateProject" again to apply the changes
    /// </summary>
    public static class MicroflowStart
    {
        /// <summary>
        /// This is the entry point, project payload is in the http body
        /// </summary>
        /// <param name="instanceId">If an instanceId is passed in, it will run as a singleton, else it will run concurrently with each with a new instanceId</param>
        [FunctionName("Microflow_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "start/{projectName}/{instanceId?}")]
                                                                HttpRequestMessage req,
                                                                [DurableClient] IDurableOrchestrationClient client,
                                                                string instanceId, string projectName)
        {
            try
            {
                //await client.PurgeInstanceHistoryAsync("7c828621-3e7a-44aa-96fd-c6946763cc2b");

                NameValueCollection data = req.RequestUri.ParseQueryString();
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

                // start
                await client.StartNewAsync("Start", instanceId, projectRun);

                return await client.WaitForCompletionOrCreateCheckStatusResponseAsync(req, instanceId, TimeSpan.FromSeconds(1));

            }
            catch (StorageException ex)
            {
                HttpResponseMessage resp = new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(ex.Message + " - Project in error state, call 'InsertOrUpdateProject' at least once before running a project.")
                };

                return resp;
            }
            catch (Exception e)
            {
                HttpResponseMessage resp = new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(e.Message)
                };

                return resp;
            }
        }

        /// <summary>
        /// This is called from Microflow_HttpStart, it does the looping and calls the ExecuteStep sub orchestration passing in the top step
        /// </summary>
        /// <returns></returns>
        [FunctionName("Start")]
        public static async Task Start([OrchestrationTrigger] IDurableOrchestrationContext context,
                                       ILogger inLog)
        {
            ILogger log = context.CreateReplaySafeLogger(inLog);

            // read ProjectRun payload
            ProjectRun projectRun = context.GetInput<ProjectRun>();

            try
            {
                if(!await context.CheckAndWaitForReadyToRun(projectRun.ProjectName, log))
                {
                    return;
                }

                // log start
                string logRowKey = MicroflowTableHelper.GetTableLogRowKeyDescendingByDate(context.CurrentUtcDateTime, "_" + projectRun.OrchestratorInstanceId);

                LogOrchestrationEntity logEntity = new LogOrchestrationEntity(true,
                                                           projectRun.ProjectName,
                                                           logRowKey,
                                                           $"{Environment.MachineName} - {projectRun.ProjectName} started...",
                                                           context.CurrentUtcDateTime,
                                                           projectRun.OrchestratorInstanceId,
                                                           projectRun.RunObject.GlobalKey);

                await context.CallActivityAsync("LogOrchestration", logEntity);

                log.LogInformation($"Started orchestration with ID = '{context.InstanceId}', Project = '{projectRun.ProjectName}'");

                await context.MicroflowStartProjectRun(log, projectRun);


                // log to table workflow completed
                logEntity = new LogOrchestrationEntity(false,
                                                       projectRun.ProjectName,
                                                       logRowKey,
                                                       $"{Environment.MachineName} - {projectRun.ProjectName} completed successfully",
                                                       context.CurrentUtcDateTime,
                                                       projectRun.OrchestratorInstanceId,
                                                       projectRun.RunObject.GlobalKey);

                Task logTask = context.CallActivityAsync("LogOrchestration", logEntity);

                EntityId projStateId = new EntityId(nameof(ProjectState), projectRun.ProjectName);
                context.SignalEntity(projStateId, "ready");

                await logTask;
                // done
                log.LogError($"Project run {projectRun.ProjectName} completed successfully...");
                log.LogError("<<<<<<<<<<<<<<<<<<<<<<<<<-----> !!! A GREAT SUCCESS  !!! <----->>>>>>>>>>>>>>>>>>>>>>>>>");
            }
            catch (StorageException e)
            {
                // log to table workflow completed
                LogErrorEntity errorEntity = new LogErrorEntity(projectRun.ProjectName, Convert.ToInt32(projectRun.RunObject.StepNumber), e.Message, projectRun.RunObject.RunId);

                await context.CallActivityAsync("LogError", errorEntity);
            }
            catch (Exception e)
            {
                // log to table workflow completed
                LogErrorEntity errorEntity = new LogErrorEntity(projectRun.ProjectName, Convert.ToInt32(projectRun.RunObject.StepNumber), e.Message, projectRun.RunObject.RunId);

                await context.CallActivityAsync("LogError", errorEntity);
            }
        }

        /// <summary>
        /// This must be called at least once before a project runs,
        /// this is to prevent multiple concurrent instances from writing step data at project run,
        /// call Microflow InsertOrUpdateProject when something changed in the workflow, but do not always call this when concurrent multiple workflows
        /// </summary>
        [FunctionName("Microflow_InsertOrUpdateProject")]
        public static async Task<HttpResponseMessage> SaveProject([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
                                                                  Route = "InsertOrUpdateProject/{globalKey?}")] HttpRequestMessage req,
                                                                  [DurableClient] IDurableEntityClient client, string globalKey)
        {
            bool doneReadyFalse = false;

            // read http content
            string content = await req.Content.ReadAsStringAsync();

            // deserialize the workflow json
            MicroflowProject project = JsonSerializer.Deserialize<MicroflowProject>(content);

            //    // create a project run
            ProjectRun projectRun = new ProjectRun() { ProjectName = project.ProjectName, Loop = project.Loop };

            EntityId projStateId = new EntityId(nameof(ProjectState), projectRun.ProjectName);

            try
            {
                Task<EntityStateResponse<int>> globStateTask = null;

                if (!string.IsNullOrWhiteSpace(globalKey))
                {
                    EntityId globalStateId = new EntityId(nameof(GlobalState), globalKey);
                    globStateTask = client.ReadEntityStateAsync<int>(globalStateId);
                }
                // do not do anything, wait for the stopped project to be ready
                var projStateTask = client.ReadEntityStateAsync<int>(projStateId);
                int globState = 0;
                if (globStateTask != null)
                {
                    await globStateTask;
                    globState = globStateTask.Result.EntityState;
                }

                var projState = await projStateTask;
                if (projState.EntityState != 0 || globState != 0)
                {
                    return new HttpResponseMessage(HttpStatusCode.Locked);
                }

                // set project ready to false
                await client.SignalEntityAsync(projStateId, "pause");
                doneReadyFalse = true;

                // reate the storage tables for the project
                await MicroflowTableHelper.CreateTables(project.ProjectName);

                // upsert project control
                //Task projTask = MicroflowTableHelper.UpdateProjectControl(project.ProjectName, 0);

                //  clear step table data
                Task delTask = projectRun.DeleteSteps();

                //    // parse the mergefields
                content.ParseMergeFields(ref project);

                //await projTask;

                await delTask;

                // prepare the workflow by persisting parent info to table storage
                await projectRun.PrepareWorkflow(project.Steps, project.StepIdFormat);

                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
            catch (StorageException e)
            {
                HttpResponseMessage resp = new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(e.Message)
                };

                return resp;
            }
            catch (Exception e)
            {
                HttpResponseMessage resp = new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(e.Message)
                };

                try
                {
                    await MicroflowHelper.LogError(project.ProjectName ?? "no project", projectRun.RunObject.GlobalKey, projectRun.RunObject.RunId, e);
                }
                catch
                {
                    resp.StatusCode = HttpStatusCode.InternalServerError;
                }

                return resp;
            }
            finally
            {
                // if project ready was set to false, always set it to true
                if (doneReadyFalse)
                {
                    await client.SignalEntityAsync(projStateId, "ready");
                }
            }
        }

        /// <summary>
        /// Get global state
        /// </summary>
        [FunctionName("getGlobalState")]
        public static async Task<HttpResponseMessage> GetGlobalState([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
                                                                  Route = "GlobalState/{globalKey}")] HttpRequestMessage req,
                                                                  [DurableClient] IDurableEntityClient client, string globalKey)
        {
            EntityId globalStateId = new EntityId(nameof(GlobalState), globalKey);
            Task<EntityStateResponse<int>> stateTask = client.ReadEntityStateAsync<int>(globalStateId);

            HttpResponseMessage resp = new HttpResponseMessage(HttpStatusCode.OK);

            await stateTask;

            resp.Content = new StringContent(stateTask.Result.EntityState.ToString());

            return resp;
        }

        /// <summary>
        /// Get project state
        /// </summary>
        [FunctionName("getProjectState")]
        public static async Task<HttpResponseMessage> GetProjectState([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
                                                                  Route = "ProjectState/{projectName}")] HttpRequestMessage req,
                                                                  [DurableClient] IDurableEntityClient client, string projectName)
        {
            EntityId runStateId = new EntityId(nameof(ProjectState), projectName);
            Task<EntityStateResponse<int>> stateTask = client.ReadEntityStateAsync<int>(runStateId);

            HttpResponseMessage resp = new HttpResponseMessage(HttpStatusCode.OK);

            await stateTask;

            resp.Content = new StringContent(stateTask.Result.EntityState.ToString());

            return resp;
        }

        /// <summary>
        /// Pause, run, or stop the project, cmd can be "run", "pause", or "stop"
        /// </summary>
        [FunctionName("Microflow_ProjectControl")]
        public static async Task<HttpResponseMessage> ProjectControl([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
                                                                  Route = "ProjectControl/{cmd}/{projectName}")] HttpRequestMessage req,
                                                              [DurableClient] IDurableEntityClient client, string projectName, string cmd)
        {
            EntityId runStateId = new EntityId(nameof(ProjectState), projectName);

            if (cmd.Equals("pause", StringComparison.OrdinalIgnoreCase))
            {
                await client.SignalEntityAsync(runStateId, "pause");
            }
            else if (cmd.Equals("run", StringComparison.OrdinalIgnoreCase))
            {
                await client.SignalEntityAsync(runStateId, "ready");
            }
            else if (cmd.Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                await client.SignalEntityAsync(runStateId, "stop");
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        /// <summary>
        /// Pause, run, or stop all with the same global key, cmd can be "run", "pause", or "stop"
        /// </summary>
        [FunctionName("Microflow_GlobalControl")]
        public static async Task<HttpResponseMessage> GlobalControl([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
                                                                  Route = "GlobalControl/{cmd}/{globalKey}")] HttpRequestMessage req,
                                                                  [DurableClient] IDurableEntityClient client, string globalKey, string cmd)
        {
            EntityId runStateId = new EntityId(nameof(GlobalState), globalKey);

            if (cmd.Equals("pause", StringComparison.OrdinalIgnoreCase))
            {
                await client.SignalEntityAsync(runStateId, "pause");
            }
            else if (cmd.Equals("run", StringComparison.OrdinalIgnoreCase))
            {
                await client.SignalEntityAsync(runStateId, "ready");
            }
            else if (cmd.Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                await client.SignalEntityAsync(runStateId, "stop");
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        /// <summary>
        /// Durable entity check and set if the global state
        /// </summary>
        [FunctionName("GlobalState")]
        public static void GlobalState([EntityTrigger] IDurableEntityContext ctx)
        {
            switch (ctx.OperationName)
            {
                case "ready":
                    ctx.SetState(0);
                    break;
                case "pause":
                    ctx.SetState(1);
                    break;
                case "stop":
                    ctx.SetState(2);
                    break;
                case "get":
                    ctx.Return(ctx.GetState<int>());
                    break;
            }
        }

        /// <summary>
        /// Durable entity check and set project state
        /// </summary>
        [FunctionName("ProjectState")]
        public static void ProjectState([EntityTrigger] IDurableEntityContext ctx)
        {
            switch (ctx.OperationName)
            {
                case "ready":
                    ctx.SetState(0);
                    break;
                case "pause":
                    ctx.SetState(1);
                    break;
                case "stop":
                    ctx.SetState(2);
                    break;
                case "get":
                    ctx.Return(ctx.GetState<int>());
                    break;
            }
        }
    }
}
