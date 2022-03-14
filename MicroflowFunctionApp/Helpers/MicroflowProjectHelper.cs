﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Data.Tables;
using Microflow.Models;
using MicroflowModels;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using static Microflow.Helpers.Constants;

namespace Microflow.Helpers
{
    public static class MicroflowProjectHelper
    {
        public static ProjectRun CreateProjectRun(HttpRequestMessage req, ref string instanceId, string projectName)
        {
            ProjectRun projectRun = MicroflowStartupHelper.CreateStartupProjectRun(req.RequestUri.ParseQueryString(), ref instanceId, projectName);
            string baseUrl = $"{Environment.GetEnvironmentVariable("BaseUrl")}";
            projectRun.BaseUrl = baseUrl.EndsWith('/')
                ? baseUrl.Remove(baseUrl.Length - 1)
                : baseUrl;

            return projectRun;
        }

        /// <summary>
        /// From the api call
        /// </summary>
        public static async Task<HttpResponseMessage> InserOrUpdateProject(this IDurableEntityClient client,
                                                                           string content,
                                                                           string globalKey)
        {
            bool doneReadyFalse = false;

            // deserialize the workflow json
            MicroflowProject project = JsonSerializer.Deserialize<MicroflowProject>(content);

            //    // create a project run
            ProjectRun projectRun = new ProjectRun() 
            { 
                ProjectName = project.ProjectName, 
                Loop = project.Loop 
            };

            EntityId projStateId = new EntityId(MicroflowStateKeys.ProjectStateId, projectRun.ProjectName);

            try
            {
                Task<EntityStateResponse<int>> globStateTask = null;

                if (!string.IsNullOrWhiteSpace(globalKey))
                {
                    EntityId globalStateId = new EntityId(MicroflowStateKeys.GlobalStateId, globalKey);
                    globStateTask = client.ReadEntityStateAsync<int>(globalStateId);
                }
                // do not do anything, wait for the stopped project to be ready
                var projStateTask = client.ReadEntityStateAsync<int>(projStateId);
                int globState = MicroflowStates.Ready;
                if (globStateTask != null)
                {
                    await globStateTask;
                    globState = globStateTask.Result.EntityState;
                }

                var projState = await projStateTask;
                if (projState.EntityState != MicroflowStates.Ready || globState != MicroflowStates.Ready)
                {
                    return new HttpResponseMessage(HttpStatusCode.Locked);
                }

                // set project ready to false
                await client.SignalEntityAsync(projStateId, MicroflowControlKeys.Pause);
                doneReadyFalse = true;

                // create the storage tables for the project
                await MicroflowTableHelper.CreateTables();

                //  clear step table data
                Task delTask = projectRun.DeleteSteps();

                //    // parse the mergefields
                content.ParseMergeFields(ref project);

                await delTask;

                // prepare the workflow by persisting parent info to table storage
                await projectRun.PrepareWorkflow(project);

                project.Steps = null;
                project.ProjectName= null;
                string projectConfigJson = JsonSerializer.Serialize(project);

                // create the storage tables for the project
                await MicroflowTableHelper.UpsertProjectConfigString(projectRun.ProjectName, projectConfigJson);

                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
            catch (Azure.RequestFailedException e)
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
                    _ = await MicroflowHelper.LogError(project.ProjectName
                                                       ?? "no project",
                                                       projectRun.RunObject.GlobalKey,
                                                       projectRun.RunObject.RunId,
                                                       e);
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
                    await client.SignalEntityAsync(projStateId, MicroflowControlKeys.Ready);
                }
            }
        }

        /// <summary>
        /// Must be called at least once before a workflow creation or update,
        /// do not call this repeatedly when running multiple concurrent instances,
        /// only call this to create a new workflow or to update an existing 1
        /// Saves step meta data to table storage and read during execution
        /// </summary>
        public static async Task PrepareWorkflow(this ProjectRun projectRun, MicroflowProject project)
        {
            List<TableTransactionAction> batch = new List<TableTransactionAction>();
            List<Task> batchTasks = new List<Task>();
            TableClient stepsTable = MicroflowTableHelper.GetStepsTable();
            Step stepContainer = new Step(-1, null);
            StringBuilder sb = new StringBuilder();
            List<Step> steps = project.Steps;
            List<(int StepNumber, int ParentCount)> liParentCounts = new List<(int, int)>();

            foreach (Step step in steps)
            {
                int count = steps.Count(c => c.SubSteps.Contains(step.StepNumber));
                liParentCounts.Add((step.StepNumber, count));
            }

            for (int i = 0; i < steps.Count; i++)
            {
                Step step = steps.ElementAt(i);

                int parentCount = liParentCounts.FirstOrDefault(s => s.StepNumber == step.StepNumber).ParentCount;

                if (parentCount == 0)
                {
                    stepContainer.SubSteps.Add(step.StepNumber);
                }

                foreach (int subId in step.SubSteps)
                {
                    int subParentCount = liParentCounts.FirstOrDefault(s => s.StepNumber.Equals(subId)).ParentCount;

                    sb.Append(subId).Append(',').Append(subParentCount).Append(';');
                }

                if (step.RetryOptions != null)
                {
                    HttpCallWithRetries httpCallRetriesEntity = new HttpCallWithRetries(projectRun.ProjectName, step.StepNumber.ToString(), step.StepId, sb.ToString())
                    {
                        CallbackAction = step.CallbackAction,
                        StopOnActionFailed = step.StopOnActionFailed,
                        CalloutUrl = step.CalloutUrl,
                        CallbackTimeoutSeconds = step.CallbackTimeoutSeconds,
                        CalloutTimeoutSeconds = step.CalloutTimeoutSeconds,
                        IsHttpGet = step.IsHttpGet,
                        AsynchronousPollingEnabled = step.AsynchronousPollingEnabled,
                        ScaleGroupId = step.ScaleGroupId
                    };

                    httpCallRetriesEntity.RetryDelaySeconds = step.RetryOptions.DelaySeconds;
                    httpCallRetriesEntity.RetryMaxDelaySeconds = step.RetryOptions.MaxDelaySeconds;
                    httpCallRetriesEntity.RetryMaxRetries = step.RetryOptions.MaxRetries;
                    httpCallRetriesEntity.RetryTimeoutSeconds = step.RetryOptions.TimeOutSeconds;
                    httpCallRetriesEntity.RetryBackoffCoefficient = step.RetryOptions.BackoffCoefficient;

                    // batchop
                    batch.Add(new TableTransactionAction(TableTransactionActionType.UpsertReplace, httpCallRetriesEntity));
                }
                else
                {
                    HttpCall httpCallEntity = new HttpCall(projectRun.ProjectName, step.StepNumber.ToString(), step.StepId, sb.ToString())
                    {
                        CallbackAction = step.CallbackAction,
                        StopOnActionFailed = step.StopOnActionFailed,
                        CalloutUrl = step.CalloutUrl,
                        CallbackTimeoutSeconds = step.CallbackTimeoutSeconds,
                        CalloutTimeoutSeconds = step.CalloutTimeoutSeconds,
                        IsHttpGet = step.IsHttpGet,
                        AsynchronousPollingEnabled = step.AsynchronousPollingEnabled,
                        ScaleGroupId = step.ScaleGroupId
                    };

                    // batchop
                    batch.Add(new TableTransactionAction(TableTransactionActionType.UpsertReplace, httpCallEntity));
                }

                sb.Clear();

                if (batch.Count == 100)
                {
                    batchTasks.Add(stepsTable.SubmitTransactionAsync(batch)); 
                    batch = new List<TableTransactionAction>();
                }
            }

            foreach (int subId in stepContainer.SubSteps)
            {
                sb.Append(subId).Append(",1;");
            }

            HttpCall containerEntity = new HttpCall(projectRun.ProjectName, "-1", null, sb.ToString());

            batch.Add(new TableTransactionAction(TableTransactionActionType.UpsertReplace, containerEntity));

            batchTasks.Add(stepsTable.SubmitTransactionAsync(batch));

            TableEntity mergeFieldsEnt = new TableEntity($"{projectRun.ProjectName}_MicroflowMergeFields", "");
            await stepsTable.UpsertEntityAsync(mergeFieldsEnt);

            await Task.WhenAll(batchTasks);
        }

        /// <summary>
        /// Parse all the merge fields in the project
        /// </summary>
        public static void ParseMergeFields(this string strWorkflow, ref MicroflowProject project)
        {
            StringBuilder sb = new StringBuilder(strWorkflow);

            foreach (KeyValuePair<string, string> field in project.MergeFields)
            {
                sb.Replace("{" + field.Key + "}", field.Value);
            }

            project = JsonSerializer.Deserialize<MicroflowProject>(sb.ToString());
        }
    }
}
