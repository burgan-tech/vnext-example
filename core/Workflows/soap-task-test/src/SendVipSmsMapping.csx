using System;
using System.Collections.Generic;
using System.Security;
using System.Threading.Tasks;
using System.Xml;
using BBT.Workflow.Scripting;
using BBT.Workflow.Definitions;

// Builds and sends the VIP SMS SOAP request, then parses the SOAP response in OutputHandler.
// SECURITY: the password is a placeholder (__TEST_PASSWORD__) or read from instance data;
// never hardcode a real credential here. USERCODE is the fixed integration user "EUROBANK".
public class SendVipSmsMapping : IMapping
{
    private const string UserCode = "EUROBANK";

    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        try
        {
            var soapTask = task as SoapTask;
            if (soapTask == null)
                throw new InvalidOperationException("Task must be a SoapTask");

            var gsmNo = (string)context.Instance?.Data?.gsmNo ?? string.Empty;
            var message = (string)context.Instance?.Data?.message ?? string.Empty;
            var header = (string)context.Instance?.Data?.header ?? "BURGAN BANK";
            var duration = (int?)context.Instance?.Data?.duration ?? 300;

            bool isNotification = true;
            try { isNotification = (bool?)(context.Instance?.Data?.isNotification) ?? true; } catch { isNotification = true; }

            // Password placeholder; real value is injected outside source control or via instance data.
            string password = "__TEST_PASSWORD__";
            try
            {
                var instancePassword = (string)context.Instance?.Data?.password;
                if (!string.IsNullOrWhiteSpace(instancePassword))
                    password = instancePassword;
            }
            catch { }

            var xml =
                "<SMS>" +
                    "<USERCODE>" + SecurityElement.Escape(UserCode) + "</USERCODE>" +
                    "<PASSWORD>" + SecurityElement.Escape(password) + "</PASSWORD>" +
                    "<GSMNO>" + SecurityElement.Escape(gsmNo) + "</GSMNO>" +
                    "<MESSAGE>" + SecurityElement.Escape(message) + "</MESSAGE>" +
                    "<HEADER>" + SecurityElement.Escape(header) + "</HEADER>" +
                    "<DURATION>" + duration + "</DURATION>" +
                    "<ISNOTIFICATION>" + (isNotification ? "true" : "false") + "</ISNOTIFICATION>" +
                    "<ONNETSIMCHANGE>True</ONNETSIMCHANGE>" +
                    "<ONNETPORTINCONTROL>True</ONNETPORTINCONTROL>" + 
                "</SMS>";

            soapTask.SetBody(xml);
            soapTask.AddHeader("Content-Type", "text/xml; charset=utf-8");
            soapTask.SetSoapAction("VipSmsSender");

            return Task.FromResult(new ScriptResponse());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "send-vip-sms-input-error",
                Data = new { error = ex.Message }
            });
        }
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        try
        {
            var statusCode = (int?)(context.Body?.statusCode) ?? 200;

            string rawXml = null;
            try { rawXml = (string)(context.Body?.data); } catch { rawXml = null; }
            if (string.IsNullOrWhiteSpace(rawXml))
            {
                try { rawXml = (string)(context.Body); } catch { rawXml = null; }
            }

            bool hasFault = false;
            bool parseError = false;
            string faultCode = null;
            string faultString = null;
            string messageId = null;
            // VipSmsSender business result: <VIPSMS><SMS><RETURNCODE>0</RETURNCODE>
            // <RETURNMESSAGE>...</RETURNMESSAGE><MESSAGEID>...</MESSAGEID></SMS></VIPSMS>
            // RETURNCODE == "0" means accepted by the SMS center; anything else is a business failure.
            string returnCode = null;
            string returnMessage = null;

            if (!string.IsNullOrWhiteSpace(rawXml))
            {
                try
                {
                    var doc = new XmlDocument();
                    doc.LoadXml(rawXml);

                    // Transport-level SOAP fault (e.g. HTTP 500) — keep as a fallback.
                    var faultNode = doc.SelectSingleNode("//*[local-name()='Fault']");
                    if (faultNode != null)
                    {
                        hasFault = true;
                        var faultCodeNode = faultNode.SelectSingleNode(".//*[local-name()='faultcode']")
                            ?? faultNode.SelectSingleNode(".//*[local-name()='Code']");
                        var faultStringNode = faultNode.SelectSingleNode(".//*[local-name()='faultstring']")
                            ?? faultNode.SelectSingleNode(".//*[local-name()='Reason']");
                        faultCode = faultCodeNode?.InnerText;
                        faultString = faultStringNode?.InnerText;
                    }

                    // VipSmsSender application result.
                    var returnCodeNode = doc.SelectSingleNode("//*[local-name()='RETURNCODE']");
                    var returnMessageNode = doc.SelectSingleNode("//*[local-name()='RETURNMESSAGE']");
                    var messageIdNode = doc.SelectSingleNode("//*[local-name()='MESSAGEID']")
                        ?? doc.SelectSingleNode("//*[local-name()='MessageId']")
                        ?? doc.SelectSingleNode("//*[local-name()='messageId']");

                    returnCode = returnCodeNode?.InnerText?.Trim();
                    returnMessage = returnMessageNode?.InnerText?.Trim();
                    messageId = messageIdNode?.InnerText?.Trim();
                }
                catch (Exception parseEx)
                {
                    parseError = true;
                    faultString = "Response parse error: " + parseEx.Message;
                }
            }

            // Business success requires a 2xx transport status, no SOAP fault, no parse error,
            // and RETURNCODE explicitly "0". A missing/non-zero RETURNCODE is treated as failure.
            bool returnOk = returnCode == "0";
            bool succeeded = statusCode >= 200 && statusCode < 300 && !hasFault && !parseError && returnOk;

            // Surface the business return code/message as fault info when the call did not succeed.
            if (!succeeded && !hasFault && !parseError && returnCode != null)
            {
                faultCode = returnCode;
                faultString = returnMessage;
            }

            return Task.FromResult(new ScriptResponse
            {
                Key = succeeded ? "vip-sms-succeeded" : "vip-sms-failed",
                Data = new
                {
                    smsResult = new
                    {
                        success = succeeded,
                        statusCode = statusCode,
                        returnCode = returnCode,
                        returnMessage = returnMessage,
                        responseText = rawXml,
                        faultCode = faultCode,
                        faultString = faultString,
                        messageId = messageId
                    }
                },
                Tags = new[] { "soap", "sms", succeeded ? "success" : (hasFault || parseError ? "fault" : "failure") }
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ScriptResponse
            {
                Key = "vip-sms-failed",
                Data = new
                {
                    smsResult = new
                    {
                        success = false,
                        statusCode = 0,
                        responseText = (string)null,
                        faultCode = (string)null,
                        faultString = ex.Message,
                        messageId = (string)null
                    }
                },
                Tags = new[] { "soap", "sms", "failure" }
            });
        }
    }
}