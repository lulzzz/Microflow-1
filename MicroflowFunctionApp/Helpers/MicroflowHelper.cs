﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MicroflowModels;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;

namespace Microflow.Helpers
{
    public static class MicroflowHelper
    {
        public static RetryOptions GetRetryOptions(HttpCallWithRetries httpCallWithRetries)
        {
            RetryOptions ops = new RetryOptions(TimeSpan.FromSeconds(httpCallWithRetries.Retry_DelaySeconds), httpCallWithRetries.Retry_MaxRetries);
            ops.RetryTimeout = TimeSpan.FromSeconds(httpCallWithRetries.Retry_TimeoutSeconds);
            ops.MaxRetryInterval = TimeSpan.FromSeconds(httpCallWithRetries.Retry_MaxDelaySeconds);
            ops.BackoffCoefficient = httpCallWithRetries.Retry_BackoffCoefficient;

            return ops;
        }

        /// <summary>
        /// Called before a workflow executes and takes the top step and recursives it to insert step configs into table storage
        /// </summary>
        public static async Task PrepareWorkflow(string instanceId, ProjectRun projectRun, List<Step> steps, Dictionary<string, string> mergeFields)
        {
            HashSet<KeyValuePair<int, int>> hsStepCounts = new HashSet<KeyValuePair<int, int>>();

            Local(steps[0]);

            void Local(Step step)
            {
                if (step.SubSteps != null)
                {
                    foreach (var cstep in step.SubSteps)
                    {
                        hsStepCounts.Add(new KeyValuePair<int, int>(step.StepId, cstep));
                        Local(steps[cstep - 1]);
                    }
                }
                else
                {
                    step.SubSteps = new List<int>();
                }
            }

            var tasks = new List<Task>();
            var stepsTable = MicroflowTableHelper.GetStepsTable(projectRun.ProjectName);

            Step stepContainer = new Step(-1, "");
            steps.Insert(0, stepContainer);

            for (int i = 0; i < steps.Count; i++)
            {
                Step step = steps.ElementAt(i);

                if (step.StepId > -1)
                {
                    var parents = steps.Where(x => x.SubSteps.Contains(step.StepId)).ToList();
                    if (parents.Count == 0)
                    {
                        stepContainer.SubSteps.Add(step.StepId);
                    }

                    List<KeyValuePair<int, int>> substeps = new List<KeyValuePair<int, int>>();

                    foreach (var sub in step.SubSteps)
                    {
                        var count = hsStepCounts.Count(x => x.Value == sub);
                        substeps.Add(new KeyValuePair<int, int>(sub, count));
                    }

                    if (step.RetryOptions != null)
                    {
                        HttpCallWithRetries stentRetries = new HttpCallWithRetries(projectRun.ProjectName, step.StepId, JsonSerializer.Serialize(substeps))
                        {
                            CallBackAction = step.CallbackAction,
                            StopOnActionFailed = step.StopOnActionFailed,
                            Url = step.CalloutUrl,
                            ActionTimeoutSeconds = step.ActionTimeoutSeconds,
                            IsHttpGet = step.IsHttpGet
                        };

                        stentRetries.Retry_DelaySeconds = step.RetryOptions.DelaySeconds;
                        stentRetries.Retry_MaxDelaySeconds = step.RetryOptions.MaxDelaySeconds;
                        stentRetries.Retry_MaxRetries = step.RetryOptions.MaxRetries;
                        stentRetries.Retry_TimeoutSeconds = step.RetryOptions.TimeOutSeconds;
                        stentRetries.Retry_BackoffCoefficient = step.RetryOptions.BackoffCoefficient;

                        tasks.Add(MicroflowTableHelper.InsertStep(stentRetries, stepsTable));
                    }
                    else
                    {
                        HttpCall stent = new HttpCall(projectRun.ProjectName, step.StepId, JsonSerializer.Serialize(substeps))
                        {
                            CallBackAction = step.CallbackAction,
                            StopOnActionFailed = step.StopOnActionFailed,
                            Url = step.CalloutUrl,
                            ActionTimeoutSeconds = step.ActionTimeoutSeconds,
                            IsHttpGet = step.IsHttpGet
                        };

                        tasks.Add(MicroflowTableHelper.InsertStep(stent, stepsTable));
                    }
                }
            }

            List<KeyValuePair<int, int>> containersubsteps = new List<KeyValuePair<int, int>>();
            foreach (var substep in stepContainer.SubSteps)
            {
                containersubsteps.Add(new KeyValuePair<int, int>(substep, 1));
            }

            HttpCall containerEntity = new HttpCall(projectRun.ProjectName, -1, JsonSerializer.Serialize(containersubsteps));
            tasks.Add(MicroflowTableHelper.InsertStep(containerEntity, stepsTable));

            await Task.WhenAll(tasks);
        }

        public static void ParseMergeFields(string strWorkflow, ref Project project)
        {
            StringBuilder sb = new StringBuilder(strWorkflow);

            foreach (var field in project.MergeFields)
            {
                sb.Replace("{" + field.Key + "}", field.Value);
            }

            project = JsonSerializer.Deserialize<Project>(sb.ToString());
        }

        public static DurableHttpRequest CreateMicroflowDurableHttpRequest(HttpCall httpCall, string instanceId)
        {
            string callback = string.IsNullOrWhiteSpace(httpCall.CallBackAction)
                    ? ""
                    : $"{Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME")}/api/{httpCall.CallBackAction}/{instanceId}/{httpCall.RowKey}";

            if (!httpCall.IsHttpGet)
            {
                MicroflowPostData postData = new MicroflowPostData()
                {
                    ProjectName = httpCall.PartitionKey,
                    SubOrchestrationId = instanceId,
                    RunId = httpCall.RunId,
                    StepId = httpCall.RowKey,
                    MainOrchestrationId = httpCall.MainOrchestrationId,
                    CallbackUrl = callback
                };

                string body = JsonSerializer.Serialize(postData);

                DurableHttpRequest newDurableHttpRequest = new DurableHttpRequest(
                    method: HttpMethod.Post,
                    uri: new Uri(ParseUrlMicroflowData(httpCall, instanceId, postData.CallbackUrl)),
                    timeout: TimeSpan.FromSeconds(httpCall.ActionTimeoutSeconds),
                    //headers: durableHttpRequest.Headers,
                    content: body 
                    //tokenSource: durableHttpRequest.TokenSource

                );

                // Do not copy over the x-functions-key header, as in many cases, the
                // functions key used for the initial request will be a Function-level key
                // and the status endpoint requires a master key.
                //newDurableHttpRequest.Headers.Remove("x-functions-key");

                return newDurableHttpRequest;
            }
            else
            {
                DurableHttpRequest newDurableHttpRequest = new DurableHttpRequest(
                    method: HttpMethod.Get,
                    uri: new Uri(ParseUrlMicroflowData(httpCall, instanceId, callback)),
                    timeout: TimeSpan.FromSeconds(httpCall.ActionTimeoutSeconds)
                //headers: durableHttpRequest.Headers,
                              //tokenSource: durableHttpRequest.TokenSource

                );

                // Do not copy over the x-functions-key header, as in many cases, the
                // functions key used for the initial request will be a Function-level key
                // and the status endpoint requires a master key.
                //newDurableHttpRequest.Headers.Remove("x-functions-key");

                return newDurableHttpRequest;
            }
        }

        private static string ParseUrlMicroflowData(HttpCall httpCall, string instanceId, string callbackUrl)
        {
            StringBuilder sb = new StringBuilder(httpCall.Url);

            sb.Replace("<ProjectName>", httpCall.PartitionKey);
            sb.Replace("<MainOrchestrationId>", httpCall.MainOrchestrationId);
            sb.Replace("<SubOrchestrationId>", instanceId);
            sb.Replace("<CallbackUrl>", callbackUrl);
            sb.Replace("<RunId>", httpCall.RunId);
            sb.Replace("<StepId>", httpCall.RowKey);

            return sb.ToString();
        }
    }
}
