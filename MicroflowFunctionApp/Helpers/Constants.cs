﻿using System;

namespace Microflow.Helpers
{
    public static class Constants
    {
        public static class CallNames
        {
            public static readonly string CallbackBase = $"{Environment.GetEnvironmentVariable("CallbackBase")}";
            public const string CanExecuteNow = "CanExecuteNow";
            public const string CanExecuteNowInScaleGroup = "CanExecuteNowInScaleGroup";
            public const string CanExecuteNowInScaleGroupCount = "CanExecuteNowInScaleGroupCount";
            public const string ScaleGroupMaxConcurrentInstanceCount = "ScaleGroupMaxConcurrentInstanceCount";
            public const string ExecuteStep = "ExecuteStep";
            public const string GetStep = "GetStep";
            public const string LogError = "LogError";
            public const string LogStep = "LogStep";
            public const string LogOrchestration = "LogOrchestration";
        }

        public static class MicroflowStates
        {
            public const int Ready = 0;
            public const int Paused = 1;
            public const int Stopped = 2;
        }

        public static class MicroflowEntities
        {
            public const string StepCount = "StepCount";
            public const string CanExecuteNowCount = "CanExecuteNowCount";
        }

        public static class MicroflowCounterKeys
        {
            public const string Add = "add";
            public const string Subtract = "subtract";
            public const string Read = "get";
        }

        public static class MicroflowStateKeys
        {
            public const string ProjectState = "ProjectState";
            public const string GlobalState = "GlobalState";
        }

        public static class MicroflowControlKeys
        {
            public const string Ready = "ready";
            public const string Pause = "pause";
            public const string Stop = "stop";
            public const string Read = "get";
        }

        public static readonly char[] Splitter = { ',', ';' };
}
}
