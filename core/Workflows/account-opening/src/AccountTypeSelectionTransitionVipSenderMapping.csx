using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Functions;

public class AccountTypeSelectionTransitionVipSenderMapping : ScriptBase, IMapping
{
    public async Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var soapTask = (task as SoapTask);
        var gsmNo = context.Body.gsmNo.ToString();
        var message = context.Body.message.ToString();

        soapTask.SetBody($"""
                          <?xml version="1.0" encoding="UTF-8"?>
                          <SMS>
                          <USERCODE>EUROBANK</USERCODE>
                          <PASSWORD>E9r6f3n</PASSWORD>
                          <GSMNO>{gsmNo}</GSMNO>
                          <DURATION>300</DURATION>
                          <HEADER>BURGAN BANK</HEADER>
                          <ISENCRYPTED>False</ISENCRYPTED>
                          <MESSAGE>{message}</MESSAGE>
                          <ONNETSIMCHANGE>True</ONNETSIMCHANGE>
                          <ONNETPORTINCONTROL>True</ONNETPORTINCONTROL>
                          <ISNOTIFICATION>True</ISNOTIFICATION>
                          </SMS>
                          """);

        return new ScriptResponse { Data = new { gsmNo } };
    }

    public async Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        var response = context.TaskResponse["vipSender"];
        var data = (Dictionary<string, object>)response.Data;

        var status = data["RETURNMESSAGE"].ToString(); // "OK"
        var msgId = data["MESSAGEID"].ToString(); // "12345"

        // Raw XML de mevcut:
        var rawXml = response.Body;

        return new ScriptResponse
        {
            Data = new { smsStatus = status, smsMsgId = msgId }
        };
        
    }
}