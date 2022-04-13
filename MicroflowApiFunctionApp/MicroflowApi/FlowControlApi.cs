﻿#if DEBUG || RELEASE || !DEBUG_NO_FLOWCONTROL && !DEBUG_NO_FLOWCONTROL_SCALEGROUPS && !DEBUG_NO_FLOWCONTROL_SCALEGROUPS_STEPCOUNT && !DEBUG_NO_FLOWCONTROL_STEPCOUNT && !DEBUG_NO_UPSERT_FLOWCONTROL && !DEBUG_NO_UPSERT_FLOWCONTROL_SCALEGROUPS && !DEBUG_NO_UPSERT_FLOWCONTROL_SCALEGROUPS_STEPCOUNT && !DEBUG_NO_UPSERT_FLOWCONTROL_STEPCOUNT && !RELEASE_NO_FLOWCONTROL && !RELEASE_NO_FLOWCONTROL_SCALEGROUPS && !RELEASE_NO_FLOWCONTROL_SCALEGROUPS_STEPCOUNT && !RELEASE_NO_FLOWCONTROL_STEPCOUNT && !RELEASE_NO_UPSERT_FLOWCONTROL && !RELEASE_NO_UPSERT_FLOWCONTROL_SCALEGROUPS && !RELEASE_NO_UPSERT_FLOWCONTROL_SCALEGROUPS_STEPCOUNT && !RELEASE_NO_UPSERT_FLOWCONTROL_STEPCOUNT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using static MicroflowModels.Constants;

namespace MicroflowApi
{
    public static class FlowControlApi
    {
        /// <summary>
        /// Pause, run, or stop the workflow, cmd can be "run", "pause", or "stop"
        /// </summary>
        [FunctionName(CallNames.StepFlowControl)]
        public static async Task<HttpResponseMessage> StepFlowControl([HttpTrigger(AuthorizationLevel.Anonymous, "get",
                                                                  Route = "StepFlowControl/{webHookKey}/{stepList}")] HttpRequestMessage req,
                                                                  [DurableClient] IDurableEntityClient client, string webHookKey, string stepList)
        {
            EntityId entId = new(MicroflowEntities.StepFlowInfo, webHookKey);

            await client.SignalEntityAsync(entId, MicroflowEntityKeys.Set, stepList.Split(',').Select(x => int.Parse(x)).ToList());

            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        /// <summary>
        /// Durable entity to check and set a custom step flow
        /// </summary>
        [FunctionName(MicroflowEntities.StepFlowInfo)]
        public static void StepFlowInfo([EntityTrigger] IDurableEntityContext ctx)
        {
            switch (ctx.OperationName)
            {
                case MicroflowEntityKeys.Read:
                    ctx.Return(ctx.GetState<List<int>>());
                    break;
                case MicroflowEntityKeys.Set:
                    ctx.SetState(ctx.GetInput<List<int>>());
                    break;
                case MicroflowEntityKeys.Delete:
                    ctx.DeleteState();
                    break;
            }
        }

        /// <summary>
        /// Pause, run, or stop the workflow, cmd can be "run", "pause", or "stop"
        /// </summary>
        [FunctionName("WorkflowControl")]
        public static async Task<HttpResponseMessage> WorkflowControl([HttpTrigger(AuthorizationLevel.Anonymous, "get",
                                                                  Route = "WorkflowControl/{cmd}/{workflowName}/{workflowVersion}")] HttpRequestMessage req,
                                                                  [DurableClient] IDurableEntityClient client, string workflowName, string cmd, string workflowVersion)
        {
            string key = string.IsNullOrWhiteSpace(workflowVersion)
                                ? workflowName
                                : $"{workflowName}@{workflowVersion}";

            if (cmd.Equals(MicroflowControlKeys.Read, StringComparison.OrdinalIgnoreCase))
            {
                EntityId projStateId = new(MicroflowStateKeys.WorkflowState, key);
                EntityStateResponse<string> stateRes = await client.ReadEntityStateAsync<string>(projStateId);

                HttpResponseMessage resp = new(HttpStatusCode.OK)
                {
                    Content = new StringContent(stateRes.EntityState)
                };

                return resp;
            }

            return await client.SetRunState(nameof(WorkflowState), key, cmd);
        }

        /// <summary>
        /// Pause, run, or stop all with the same global key, cmd can be "run", "pause", or "stop"
        /// </summary>
        [FunctionName("GlobalControl")]
        public static async Task<HttpResponseMessage> GlobalControl([HttpTrigger(AuthorizationLevel.Anonymous, "get",
                                                                  Route = "GlobalControl/{cmd}/{globalKey}")] HttpRequestMessage req,
                                                                  [DurableClient] IDurableEntityClient client, string globalKey, string cmd)
        {
            if (cmd.Equals(MicroflowControlKeys.Read, StringComparison.OrdinalIgnoreCase))
            {
                EntityId globStateId = new(MicroflowStateKeys.GlobalState, globalKey);
                EntityStateResponse<string> stateRes = await client.ReadEntityStateAsync<string>(globStateId);

                HttpResponseMessage resp = new(HttpStatusCode.OK)
                {
                    Content = new StringContent(stateRes.EntityState)
                };

                return resp;
            }

            return await client.SetRunState(nameof(GlobalState), globalKey, cmd);
        }

        /// <summary>
        /// Durable to entity check and set if the global state
        /// </summary>
        [FunctionName(MicroflowStateKeys.GlobalState)]
        public static void GlobalState([EntityTrigger] IDurableEntityContext ctx)
        {
            ctx.RunState();
        }

        /// <summary>
        /// Durable to entity check and set workflow state
        /// </summary>
        [FunctionName(MicroflowStateKeys.WorkflowState)]
        public static void WorkflowState([EntityTrigger] IDurableEntityContext ctx)
        {
            ctx.RunState();
        }

        /// <summary>
        /// For workflow and global key states
        /// </summary>
        private static void RunState(this IDurableEntityContext ctx)
        {
            switch (ctx.OperationName)
            {
                case MicroflowControlKeys.Ready:
                    ctx.SetState(MicroflowStates.Ready);
                    break;
                case MicroflowControlKeys.Pause:
                    ctx.SetState(MicroflowStates.Paused);
                    break;
                case MicroflowControlKeys.Stop:
                    ctx.SetState(MicroflowStates.Stopped);
                    break;
                case MicroflowControlKeys.Read:
                    ctx.Return(ctx.GetState<int>());
                    break;
            }
        }

        /// <summary>
        /// Set the global or workflow state with the key, and the cmd can be "pause", "ready", or "stop"
        /// </summary>
        public static async Task<HttpResponseMessage> SetRunState(this IDurableEntityClient client,
                                                                   string stateEntityId,
                                                                   string key,
                                                                   string cmd)
        {
            EntityId runStateId = new(stateEntityId, key);

            switch (cmd)
            {
                case MicroflowControlKeys.Pause:
                    await client.SignalEntityAsync(runStateId, MicroflowControlKeys.Pause);
                    break;
                case MicroflowControlKeys.Ready:
                    await client.SignalEntityAsync(runStateId, MicroflowControlKeys.Ready);
                    break;
                case MicroflowControlKeys.Stop:
                    await client.SignalEntityAsync(runStateId, MicroflowControlKeys.Stop);
                    break;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
#endif