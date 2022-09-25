using MicroflowModels;
using MicroflowSDK;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace MicroflowTest
{
    [TestClass]
    public class TestRetries
    {
        [TestMethod]
        public async Task CreateTestReties()
        {
            List<Step> workflow = TestWorkflowHelper.CreateTestWorkflow_SimpleSteps();

            (MicroflowModels.Microflow workflow, string workflowName) microflow = TestWorkflowHelper.CreateMicroflow(workflow);

            List<Task<HttpResponseMessage>> tasks = new();

            int loop = 1;
            string globalKey = Guid.NewGuid().ToString();

            string webhookId = $"{microflow.workflowName}@1@managerApproval@test";
            microflow.workflow.Step(1).SetWebhook("webhook", webhookId);

            microflow.workflow.Step(1).StopOnWebhookFailed = false;

            microflow.workflow.Step(1).WebhookTimeoutSeconds = 3;
            microflow.workflow.Step(1).RetryOptions = new MicroflowRetryOptions() { BackoffCoefficient = 1, DelaySeconds = 1, MaxDelaySeconds = 1, MaxRetries = 2, TimeOutSeconds = 300 };

            // Upsert
            bool successUpsert = await TestWorkflowHelper.UpsertWorkFlow(microflow.workflow);

            Assert.IsTrue(successUpsert);

            // start the upserted Microflow
            (string instanceId, string statusUrl) startResult = await TestWorkflowHelper.StartMicroflow(microflow, tasks, loop, globalKey);

            List<Microflow.MicroflowTableModels.LogOrchestrationEntity> log = await LogReader.GetOrchLog(microflow.workflowName);

            Assert.IsTrue(log.FindIndex(i=>i.OrchestrationId.Equals(startResult.instanceId))>=0);

            List<Microflow.MicroflowTableModels.LogStepEntity> steps = await LogReader.GetStepsLog(microflow.workflowName, startResult.instanceId);

            List<Microflow.MicroflowTableModels.LogStepEntity> s = steps.OrderBy(e => e.EndDate).ToList();
            
            Assert.IsTrue(s[0].StepNumber == 1);
            
            if(s[1].StepNumber==2)
                Assert.IsTrue(s[2].StepNumber==3);
            else
            {
                Assert.IsTrue(s[1].StepNumber == 3);
                Assert.IsTrue(s[2].StepNumber == 2);
            }

            Assert.IsTrue(s[3].StepNumber == 4);
        }
    }
}