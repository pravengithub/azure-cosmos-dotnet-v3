﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.Tracing;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Text.RegularExpressions;
    using Microsoft.Azure.Cosmos.Tracing;

    /// <summary>
    /// It is a custom listener for Activities and Event. It is used to validate the Activities generated by cosmosDb SDK.
    /// </summary>
    internal class CustomListener :
        EventListener, // Override Event Listener to capture Event source events
        IObserver<KeyValuePair<string, object>>, // Override IObserver to capture Activity events
        IObserver<DiagnosticListener>,
        IDisposable
    {
        private readonly Func<string, bool> sourceNameFilter;
        private readonly string eventName;
        
        private ConcurrentBag<IDisposable> subscriptions = new();
        private ConcurrentBag<ProducedDiagnosticScope> Scopes { get; } = new();
        
        public static ConcurrentBag<Activity> CollectedActivities { private set; get; } = new();
        private static ConcurrentBag<string> CollectedEvents { set; get; } = new();

        public CustomListener(string name, string eventName)
            : this(n => Regex.Match(n, name).Success, eventName)
        {
        }

        public CustomListener(Func<string, bool> filter, string eventName)
        {
            this.sourceNameFilter = filter;
            this.eventName = eventName;
            
            DiagnosticListener.AllListeners.Subscribe(this);
        }

        /// <summary>
        /// IObserver Override
        /// </summary>
        public void OnCompleted()
        {
            // Unimplemented Method
        }

        /// <summary>
        /// IObserver Override
        /// </summary>
        public void OnError(Exception error)
        {
            // Unimplemented Method
        }

        /// <summary>
        /// IObserver Override
        /// </summary>
        public void OnNext(KeyValuePair<string, object> value)
        {
            lock (this.Scopes)
            {
                // Check for disposal
                if (this.subscriptions == null) return;

                string startSuffix = ".Start";
                string stopSuffix = ".Stop";
                string exceptionSuffix = ".Exception";
                
                if (value.Key.EndsWith(startSuffix))
                {
                    string name = value.Key[..^startSuffix.Length];
                    PropertyInfo propertyInfo = value.Value.GetType().GetTypeInfo().GetDeclaredProperty("Links");
                    IEnumerable<Activity> links = propertyInfo?.GetValue(value.Value) as IEnumerable<Activity> ?? Array.Empty<Activity>();

                    ProducedDiagnosticScope scope = new ProducedDiagnosticScope()
                    {
                        Name = name,
                        Activity = Activity.Current,
                        Links = links.Select(a => new ProducedLink(a.ParentId, a.TraceStateString)).ToList(),
                        LinkedActivities = links.ToList()
                    };

                    this.Scopes.Add(scope);
                }
                else if (value.Key.EndsWith(stopSuffix))
                {
                    string name = value.Key[..^stopSuffix.Length];
                    foreach (ProducedDiagnosticScope producedDiagnosticScope in this.Scopes)
                    {
                        if (producedDiagnosticScope.Activity.Id == Activity.Current.Id)
                        {
                            AssertActivity.IsValid(producedDiagnosticScope.Activity);
                            
                            CustomListener.CollectedActivities.Add(producedDiagnosticScope.Activity);

                            producedDiagnosticScope.IsCompleted = true;
                            return;
                        }
                    }
                    throw new InvalidOperationException($"Event '{name}' was not started");
                }
                else if (value.Key.EndsWith(exceptionSuffix))
                {
                    string name = value.Key[..^exceptionSuffix.Length];
                    foreach (ProducedDiagnosticScope producedDiagnosticScope in this.Scopes)
                    {
                        if (producedDiagnosticScope.Activity.Id == Activity.Current.Id)
                        {
                            if (producedDiagnosticScope.IsCompleted)
                            {
                                throw new InvalidOperationException("Scope should not be stopped when calling Failed");
                            }

                            producedDiagnosticScope.Exception = (Exception)value.Value;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// IObserver Override
        /// </summary>
        public void OnNext(DiagnosticListener value)
        {
            if (this.sourceNameFilter(value.Name) && this.subscriptions != null)
            {
                lock (this.Scopes)
                {
                    this.subscriptions?.Add(value.Subscribe(this));
                }
            }
        }

        /// <summary>
        /// EventListener Override
        /// </summary>
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource != null && eventSource.Name.Equals(this.eventName))
            {
                this.EnableEvents(eventSource, EventLevel.Informational); // Enable information level events
            }
        }

        /// <summary>
        /// EventListener Override
        /// </summary>
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("<EVENT>")
                   .Append("<EVENT-NAME>").Append(eventData.EventName).Append("</EVENT-NAME>")
                   .Append("<EVENT-TEXT>Ideally, this should contain request diagnostics but request diagnostics is " +
                   "subject to change with each request as it contains few unique id. " +
                   "So just putting this tag with this static text to make sure event is getting generated" +
                   " for each test.</EVENT-TEXT>")
                   .Append("</EVENT>");
            CustomListener.CollectedEvents.Add(builder.ToString());
        }
        
        /// <summary>
        /// Dispose Override
        /// </summary>
        /// <exception cref="InvalidOperationException"></exception>
        public override void Dispose()
        {
            base.Dispose();

            if (this.subscriptions == null)
            {
                return;
            }

            ConcurrentBag<IDisposable> subscriptions;
            lock (this.Scopes)
            {
                subscriptions = this.subscriptions;
                this.subscriptions = null;
            }

            foreach (IDisposable subscription in subscriptions)
            {
                subscription.Dispose();
            }

            foreach (ProducedDiagnosticScope producedDiagnosticScope in this.Scopes)
            {
                Activity activity = producedDiagnosticScope.Activity;
                string operationName = activity.OperationName;
                // traverse the activities and check for duplicates among ancestors
                while (activity != null)
                {
                    if (operationName == activity.Parent?.OperationName)
                    {
                        // Throw this exception lazily on Dispose, rather than when the scope is started, so that we don't trigger a bunch of other
                        // erroneous exceptions relating to scopes not being completed/started that hide the actual issue
                        throw new InvalidOperationException($"A scope has already started for event '{producedDiagnosticScope.Name}'");
                    }

                    activity = activity.Parent;
                }

                if (!producedDiagnosticScope.IsCompleted)
                {
                    throw new InvalidOperationException($"'{producedDiagnosticScope.Name}' scope is not completed");
                }
            }

            this.ResetAttributes();
        }
        
        private string GenerateTagForBaselineTest(Activity activity)
        {
            List<string> tagsWithStaticValue = new List<string>
            {
                "kind",
                "az.namespace",
                "db.operation",
                "db.system",
                "net.peer.name",
                "db.cosmosdb.connection_mode",
                "db.cosmosdb.operation_type",
                "db.cosmosdb.regions_contacted"
            };
            
            StringBuilder builder = new StringBuilder();
            builder.Append("<ACTIVITY>")
                   .Append("<OPERATION>")
                   .Append(activity.OperationName)
                   .Append("</OPERATION>");
            
            foreach (KeyValuePair<string, string> tag in activity.Tags)
            {
                builder
                .Append("<ATTRIBUTE-KEY>")
                .Append(tag.Key)
                .Append("</ATTRIBUTE-KEY>");

                if (tagsWithStaticValue.Contains(tag.Key))
                {
                    builder
                    .Append("<ATTRIBUTE-VALUE>")
                    .Append(tag.Value)
                    .Append("</ATTRIBUTE-VALUE>");
                }
            }
            
            builder.Append("</ACTIVITY>");
            
            return builder.ToString();
        }
        
        public List<string> GetRecordedAttributes() 
        {
            List<string> generatedActivityTagsForBaselineXmls = new();
            List<Activity> collectedActivities = new List<Activity>(CustomListener.CollectedActivities);

            collectedActivities.OrderBy(act => act.OperationName);
            
            foreach (Activity activity in collectedActivities)
            {
                generatedActivityTagsForBaselineXmls.Add(this.GenerateTagForBaselineTest(activity));
            }
            
            List<string> outputList = new List<string>();
            if(generatedActivityTagsForBaselineXmls != null && generatedActivityTagsForBaselineXmls.Count > 0)
            {
                outputList.AddRange(generatedActivityTagsForBaselineXmls);

            }
            if (CustomListener.CollectedEvents != null && CustomListener.CollectedEvents.Count > 0)
            {
                outputList.AddRange(CustomListener.CollectedEvents);
            }

            return outputList;
        }

        public void ResetAttributes()
        {
            CustomListener.CollectedEvents = new();
            CustomListener.CollectedActivities = new();
        }

        public class ProducedDiagnosticScope
        {
            public string Name { get; set; }
            public Activity Activity { get; set; }
            public bool IsCompleted { get; set; }
            public bool IsFailed => this.Exception != null;
            public Exception Exception { get; set; }
            public List<ProducedLink> Links { get; set; } = new List<ProducedLink>();
            public List<Activity> LinkedActivities { get; set; } = new List<Activity>();

            public override string ToString()
            {
                return this.Name;
            }
        }

        public struct ProducedLink
        {
            public ProducedLink(string id)
            {
                this.Traceparent = id;
                this.Tracestate = null;
            }

            public ProducedLink(string traceparent, string tracestate)
            {
                this.Traceparent = traceparent;
                this.Tracestate = tracestate;
            }

            public string Traceparent { get; set; }
            public string Tracestate { get; set; }
        }
    }
}
