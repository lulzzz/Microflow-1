﻿#define INCLUDE_scalegroups
#if INCLUDE_scalegroups
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static MicroflowModels.Constants;

namespace MicroflowApi
{
    public class ScaleGroupsApi
    {
        /// <summary>
        /// Get/set max instance count for scale group
        /// </summary>
        [FunctionName("ScaleGroup")]
        public static async Task<HttpResponseMessage> ScaleGroup([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
                                                                  Route = "microflow/" + MicroflowVersion + "/ScaleGroup/{scaleGroupId?}/{maxInstanceCount?}")] HttpRequestMessage req,
                                                                  [DurableClient] IDurableEntityClient client, string scaleGroupId, int? maxInstanceCount)
        {
            if (req.Method.Equals(HttpMethod.Get))
            {
                Dictionary<string, int> result = new Dictionary<string, int>();
                EntityQueryResult res = null;

                using (CancellationTokenSource cts = new CancellationTokenSource())
                {
                    res = await client.ListEntitiesAsync(new EntityQuery()
                    {
                        PageSize = 99999999,
                        EntityName = ScaleGroupCalls.ScaleGroupMaxConcurrentInstanceCount,
                        FetchState = true
                    }, cts.Token);
                }

                if (string.IsNullOrWhiteSpace(scaleGroupId))
                {
                    foreach (var rr in res.Entities)
                    {
                        result.Add(rr.EntityId.EntityKey, (int)rr.State);
                    }
                }
                else
                {
                    foreach (var rr in res.Entities.Where(e => e.EntityId.EntityKey.Equals(scaleGroupId)))
                    {
                        result.Add(rr.EntityId.EntityKey, (int)rr.State);
                    }
                }

                var content = new StringContent(JsonSerializer.Serialize(result));

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content
                };
            }

            EntityId scaleGroupCountId = new EntityId(ScaleGroupCalls.ScaleGroupMaxConcurrentInstanceCount, scaleGroupId);

            await client.SignalEntityAsync(scaleGroupCountId, MicroflowCounterKeys.Set, maxInstanceCount);

            HttpResponseMessage resp = new HttpResponseMessage(HttpStatusCode.OK);

            return resp;
        }
    }
}
#endif