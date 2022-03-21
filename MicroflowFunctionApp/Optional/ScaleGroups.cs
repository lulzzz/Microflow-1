﻿#if !DEBUG_NOUPSERT_NOFLOWCONTROL_NOSCALEGROUPS && !DEBUG_NOUPSERT_NOSCALEGROUPS
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
using static MicroflowModels.Constants.Constants;

namespace Microflow.SplitMode
{
    public class ScaleGroups
    {
        /// <summary>
     /// Get/set max instance count for scale group
     /// </summary>
        [FunctionName("ScaleGroup")]
        public static async Task<HttpResponseMessage> SetScaleGroup([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
                                                                  Route = "ScaleGroup/{scaleGroupId?}/{maxInstanceCount?}")] HttpRequestMessage req,
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
                        EntityName = CallNames.ScaleGroupMaxConcurrentInstanceCount,
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

            EntityId scaleGroupCountId = new EntityId(CallNames.ScaleGroupMaxConcurrentInstanceCount, scaleGroupId);

            await client.SignalEntityAsync(scaleGroupCountId, "set", maxInstanceCount);

            HttpResponseMessage resp = new HttpResponseMessage(HttpStatusCode.OK);

            return resp;
        }
    }
}
#endif