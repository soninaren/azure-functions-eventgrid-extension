﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Host.Config;
using Microsoft.Azure.WebJobs.Host.Executors;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.WebJobs.Extensions.EventGrid
{
    public class EventGridExtensionConfig : IExtensionConfigProvider,
                       IAsyncConverter<HttpRequestMessage, HttpResponseMessage>
    {
        private TraceWriter _tracer = null;

        public void Initialize(ExtensionConfigContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }
            else if (context.Trace == null)
            {
                throw new ArgumentNullException("context.Trace");
            }
            _tracer = context.Trace;

            Uri url = context.GetWebhookHandler();
            _tracer.Trace(new TraceEvent(System.Diagnostics.TraceLevel.Info, $"registered EventGrid Endpoint = {url?.GetLeftPart(UriPartial.Path)}"));

            // Register our extension binding providers
            context.Config.RegisterBindingExtension(new EventGridTriggerAttributeBindingProvider(this));
        }

        private Dictionary<string, EventGridListener> _listeners = new Dictionary<string, EventGridListener>();

        internal void AddListener(string key, EventGridListener listener)
        {
            _listeners.Add(key, listener);
        }

        public async Task<HttpResponseMessage> ConvertAsync(HttpRequestMessage input, CancellationToken cancellationToken)
        {
            var response = ProcessAsync(input);
            return await response;
        }

        private async Task<HttpResponseMessage> ProcessAsync(HttpRequestMessage req)
        {
            // webjobs.script uses req.GetQueryNameValuePairs();
            // which requires webapi.core...but this does not work for .netframework2.0
            // TODO change this once webjobs.script is migrated
            var functionName = HttpUtility.ParseQueryString(req.RequestUri.Query)["functionName"];
            if (String.IsNullOrEmpty(functionName) || !_listeners.ContainsKey(functionName))
            {
                _tracer.Trace(new TraceEvent(System.Diagnostics.TraceLevel.Info,
                    $"cannot find function: '{functionName}', available function names: [{string.Join(", ", _listeners.Keys.ToArray())}]"));
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"cannot find function: '{functionName}'") };
            }

            IEnumerable<string> eventTypeHeaders = null;
            string eventTypeHeader = null;
            if (req.Headers.TryGetValues("aeg-event-type", out eventTypeHeaders))
            {
                eventTypeHeader = eventTypeHeaders.First();
            }

            if (String.Equals(eventTypeHeader, "SubscriptionValidation", StringComparison.OrdinalIgnoreCase))
            {
                string jsonArray = await req.Content.ReadAsStringAsync();
                SubscriptionValidationEvent validationEvent = null;
                List<JObject> events = JsonConvert.DeserializeObject<List<JObject>>(jsonArray);
                // TODO remove unnecessary serialization
                validationEvent = ((JObject)events[0]["data"]).ToObject<SubscriptionValidationEvent>();
                SubscriptionValidationResponse validationResponse = new SubscriptionValidationResponse { ValidationResponse = validationEvent.ValidationCode };
                var returnMessage = new HttpResponseMessage(HttpStatusCode.OK);
                returnMessage.Content = new StringContent(JsonConvert.SerializeObject(validationResponse));
                _tracer.Trace(new TraceEvent(System.Diagnostics.TraceLevel.Info,
                    $"perform handshake with eventGrid for function: {functionName}"));
                return returnMessage;
            }
            else if (String.Equals(eventTypeHeader, "Notification", StringComparison.OrdinalIgnoreCase))
            {
                JArray events = null;
                string requestContent = await req.Content.ReadAsStringAsync();
                var token = JToken.Parse(requestContent);
                if (token.Type == JTokenType.Array)
                {
                    // eventgrid schema
                    events = (JArray)token;
                }
                else if (token.Type == JTokenType.Object)
                {
                    // cloudevent schema
                    events = new JArray
                    {
                        token
                    };
                }

                List<Task<FunctionResult>> executions = new List<Task<FunctionResult>>();
                foreach (var ev in events)
                {
                    // assume each event is a JObject
                    TriggeredFunctionData triggerData = new TriggeredFunctionData
                    {
                        TriggerValue = ev
                    };
                    executions.Add(_listeners[functionName].Executor.TryExecuteAsync(triggerData, CancellationToken.None));
                }
                await Task.WhenAll(executions);

                // FIXME without internal queuing, we are going to process all events in parallel
                // and return 500 if there's at least one failure...which will cause EventGrid to resend the entire payload
                foreach (var execution in executions)
                {
                    if (!execution.Result.Succeeded)
                    {
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(execution.Result.Exception.Message) };
                    }
                }

                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
            else if (String.Equals(eventTypeHeader, "Unsubscribe", StringComparison.OrdinalIgnoreCase))
            {
                // TODO disable function?
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest);

        }
    }
}
