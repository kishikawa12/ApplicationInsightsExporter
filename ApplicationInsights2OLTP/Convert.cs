﻿using System;
using Google.Protobuf;
using Opentelemetry.Proto.Trace.V1;
using Opentelemetry.Proto.Common.V1;
using Opentelemetry.Proto.Resource.V1;
using System.Text.Json;
using System.Collections.Generic;
using Opentelemetry.Proto.Collector.Trace.V1;
using System.Text;
using Newtonsoft.Json.Linq;
using OpenTelemetry;
using ApplicationInsights;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AISemConv = ApplicationInsights.SemanticConventions;
using OTelSemConv = OpenTelemetry.SemanticConventions;

namespace ApplicationInsights2OTLP
{
    //
    //AppInsights Telemetry mapping: https://github.com/open-telemetry/opentelemetry-collector-contrib/tree/main/exporter/azuremonitorexporter
    //
    public class Convert
    {
        private readonly ILogger _logger;

        public readonly bool _SimulateRealtime = false;

        public Convert(ILoggerFactory? loggerFactory)
        {
            _logger = loggerFactory?.CreateLogger<Convert>()
              ?? NullLoggerFactory.Instance.CreateLogger<Convert>();
        }
#if DEBUG
        public Convert(ILoggerFactory? loggerFactory,bool simulateRealtime): this(loggerFactory)
        {
            _SimulateRealtime = simulateRealtime;
        }
#endif
        private Span.Types.SpanKind MapSpanKind(string telemetryType, string operationType)
        {
            
            //https://github.com/Azure/azure-sdk-for-net/blob/b62fd9f5c96c12cdb60a34234086346ec871b0d3/sdk/monitor/Azure.Monitor.OpenTelemetry.Exporter/src/Internals/ActivityExtensions.cs
            //ActivityKind.Server=>TelemetryType.Request,
            //ActivityKind.Client=>TelemetryType.Dependency,
            //ActivityKind.Producer=>TelemetryType.Dependency,
            //ActivityKind.Consumer=>TelemetryType.Request,
            //_ =>TelemetryType.Dependency

            if (telemetryType == TelemetryTypes.AppRequests)
                return Span.Types.SpanKind.Server;
            else if (telemetryType == TelemetryTypes.AppDependencies)
            {
                operationType = operationType.ToLower(); 

                if (operationType.StartsWith(OperationTypes.Messaging)) 
                    return Span.Types.SpanKind.Producer;
                else
                    return Span.Types.SpanKind.Client;
            }

            return Span.Types.SpanKind.Server;
        }

        private string NormalizeKeyName(string key)
        {
            return key.ToLower().Replace(" ", "_");
        }
        private KeyValuePair<string,string>? MapProperties(string key, string value)
        {
            string? newKey = null;
            string newVal = String.Empty;

            switch (key)
            {
                //skip as already set for resource attributes
                case Properties.HostInstanceId: 
                case Properties.ProcessId:
                //skip as already set span attribute
                case Properties.OperationName:
                //skip as known to be useless
                case Properties.LogLevel:
                case Properties.Category:
                    return null;
                default:
                    newKey = AISemConv.ScopeAppInsights+"."+ AISemConv.ScopeProperties+"."+NormalizeKeyName(key); break;
            }

            if (newKey != null && String.IsNullOrEmpty(newVal))
                newVal = value;

            if (newKey != null)
                return new KeyValuePair<string, string>(newKey, newVal);
            else
                return null;
        }

        public DateTimeOffset ParseTimestamp(string ts)
        {
#if DEBUG
            if (_SimulateRealtime)
                return DateTimeOffset.UtcNow;
            else
#endif
                return DateTimeOffset.Parse(ts);
        }

        internal string ParseTraceId(string traceid)
        {
            
#if DEBUG
            if (_SimulateRealtime) //generate a unique trace-id per run 
                traceid = BitConverter.ToString(Guid.NewGuid().ToByteArray()).Replace("-", string.Empty).ToLower();
#endif

            return traceid;
        }

        public ulong ConvertTimeStampToNano(string ts)
        {
            var d = ParseTimestamp(ts);

            DateTime epochStart = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (ulong)(d - epochStart).Ticks * 100;
        }

        public ulong ConvertTimeSpanToNano(string ts, double durationMS)
        {
            var d = ParseTimestamp(ts);
            d = d.AddMilliseconds(durationMS);
            DateTime epochStart = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (ulong)(d - epochStart).Ticks * 100;
            
        }

        public bool TryAddResourceAttribute(ResourceSpans s, string key, string value)
        {
            if (!String.IsNullOrEmpty(value))
            {
                s.Resource.Attributes.Add(new KeyValue()
                {
                    Key = key,
                    Value = new AnyValue()
                    {
                        StringValue = value
                    }
                });

                return true;
            }
            return false;
        }

        public bool TryAddAttribute(Span s, string key, string value)
        {
            if (!String.IsNullOrEmpty(value))
            {
                s.Attributes.Add(new KeyValue()
                {
                    Key = key,
                    Value = new AnyValue()
                    {
                        StringValue = value
                    }
                }); ;
                return true;
            }
            return false;

        }

        public bool TryMapProperties(Span s, string key, string value)
        {
            var mapped = MapProperties(key, value);
            if (mapped.HasValue)
            {
                s.Attributes.Add(new KeyValue()
                {
                    Key = mapped.Value.Key,
                    Value = new AnyValue()
                    {
                        StringValue = mapped.Value.Value
                    }
                });

                return true;
            }

            return false;
        }

        internal ByteString ConvertToByteString(string str)
        {
            byte[] byteArray = new byte[str.Length / 2];
            for (int i = 0; i < byteArray.Length; i++)
            {
                byteArray[i] = System.Convert.ToByte(str.Substring(i * 2, 2), 16);
            }

            ByteString byteStr = ByteString.CopyFrom(byteArray);

            return byteStr;
        }

        internal string Value(JsonElement e, string key)
        {
            JsonElement val;
            if (e.TryGetProperty(key, out val))
            {
                var r = val.GetString();
                if (!String.IsNullOrEmpty(r)) 
                    return r;
            }
            else
            {
                _logger.LogDebug("Missing property '" + key + "'");
            }

            return "";

        }

        public ExportTraceServiceRequest FromApplicationInsights(string appInsightsJsonStr)
        {

            _logger.LogDebug("[Convert] [FromApplicationInsights] " + appInsightsJsonStr);

            var export = new ExportTraceServiceRequest();

            var resSpan = new ResourceSpans();
            export.ResourceSpans.Add(resSpan);

            var root = JsonDocument.Parse(appInsightsJsonStr);

            var t = root.RootElement.GetProperty(TelemetryConstants.Records).EnumerateArray();
            while (t.MoveNext())
            {
                var traceId = ParseTraceId(Value(t.Current, Attributes.OperationId));
                if (String.IsNullOrEmpty(traceId))
                {
                    _logger.LogWarning("Skip processing telemetry! Property '" + Attributes.OperationId + "' is missing");
                    continue;
                }

                var parentId = Value(t.Current, Attributes.ParentId); 
                if (parentId == Value(t.Current, Attributes.OperationId))
                    parentId = "";

                JsonElement properties;
                bool hasProperties = false;

                resSpan.Resource = new Resource();
                
                TryAddResourceAttribute(resSpan, OTelSemConv.AttributeServiceName,Value(t.Current, Attributes.AppRoleName));
                TryAddResourceAttribute(resSpan, OTelSemConv.AttributeServiceInstance, Value(t.Current, Attributes.AppRoleInstance));
                if (t.Current.TryGetProperty(TelemetryConstants.Properties, out properties))
                {
                    hasProperties = true;
                    TryAddResourceAttribute(resSpan, OTelSemConv.AttributeProcessId, Value(properties, Properties.ProcessId));
                    TryAddResourceAttribute(resSpan, OTelSemConv.AttributeHostId, Value(properties, Properties.HostInstanceId));
                }


                var libSpan = new InstrumentationLibrarySpans();

                var instr = Value(t.Current, Attributes.SDKVersion).Split(new char[] {':'});

                libSpan.InstrumentationLibrary = new InstrumentationLibrary()
                {
                    Name = instr[0] ?? ApplicationInsights.SemanticConventions.InstrumentationLibraryName,
                    Version = instr[1] ?? String.Empty
                };
                resSpan.InstrumentationLibrarySpans.Add(libSpan);

                var span = new Span();
                libSpan.Spans.Add(span);

                _logger.LogDebug("[Convert] [FromApplicationInsights] ConvertToByteString TraceId ");
                span.TraceId = ConvertToByteString(traceId);
                _logger.LogDebug("[Convert] [FromApplicationInsights] ConvertToByteString SpanId ");
                span.SpanId = ConvertToByteString(Value(t.Current, Attributes.Id));
                if (!String.IsNullOrEmpty(parentId))
                    _logger.LogDebug("[Convert] [FromApplicationInsights] ConvertToByteString ParentId ");
                    span.ParentSpanId = ConvertToByteString(parentId);

                var spanType = Value(t.Current, Attributes.Type);
                string operation = Value(t.Current, Attributes.OperationName);

                if (spanType == TelemetryTypes.AppDependencies)
                {
                    try
                    {
                        string dt = Value(t.Current, Attributes.DependencyType);

                        span.Kind = MapSpanKind(spanType, dt);

                        TryAddAttribute(span, ApplicationInsights.SemanticConventions.DependencyType, dt);

                        if (dt == DependencyTypes.Http || dt == DependencyTypes.HttpTracked)
                        {
                            var req = Value(t.Current, Attributes.Name).Split(new char[] { ' ' });

                            operation = "HTTP " + req[0];
                            TryAddAttribute(span, OTelSemConv.AttributeHttpMethod, req[0]);
                            TryAddAttribute(span, OTelSemConv.AttributeHttpTarget, req[1]);
                            TryAddAttribute(span, OTelSemConv.AttributeHttpUrl, Value(t.Current, Attributes.Data));
                            TryAddAttribute(span, OTelSemConv.AttributeHttpStatusCode, Value(t.Current, Attributes.ResultCode));
                        }
                        else if (dt == DependencyTypes.Backend)
                        {
                            var req = Value(t.Current, Attributes.Name).Split(new char[] { ' ' });

                            operation = "HTTP " + req[0];
                            TryAddAttribute(span, OTelSemConv.AttributeHttpMethod, req[0]);
                            TryAddAttribute(span, OTelSemConv.AttributeHttpTarget, Value(t.Current, Attributes.Target));
                            TryAddAttribute(span, OTelSemConv.AttributeHttpStatusCode, Value(t.Current, Attributes.ResultCode));
                        }

                        else
                        {
                            operation = dt;
                            TryAddAttribute(span, AISemConv.Name, Value(t.Current, Attributes.Name));
                            TryAddAttribute(span, AISemConv.Url, Value(t.Current,Attributes.Url));
                            TryAddAttribute(span, AISemConv.Data, Value(t.Current, Attributes.Data));
                            TryAddAttribute(span, AISemConv.Status, Value(t.Current, Attributes.Status));
                            TryAddAttribute(span, AISemConv.ResultCode, Value(t.Current, Attributes.ResultCode));
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Error parsing telemetry of type 'AppDependency'");
                    }

                }
                else
                {
                    span.Kind = MapSpanKind(spanType, String.Empty);
                }

                span.Name = operation;
                _logger.LogDebug("[Convert] [FromApplicationInsights] ConvertTimeStampToNano StartTime ");
                span.StartTimeUnixNano = ConvertTimeStampToNano(Value(t.Current, Attributes.Time));
                _logger.LogDebug("[Convert] [FromApplicationInsights] ConvertTimeStampToNano EndTime ");
                span.EndTimeUnixNano = ConvertTimeSpanToNano(Value(t.Current, Attributes.Time), t.Current.GetProperty(Attributes.Duration).GetDouble());

                if (hasProperties)
                {
                    var l = properties.EnumerateObject();
                    while (l.MoveNext())
                    {
                        TryMapProperties(span, l.Current.Name, l.Current.Value.GetString());
                    }
                }
                
            }

            _logger.LogDebug("[Convert] [FromApplicationInsights] returns ");
            return export;
        }
    }
}