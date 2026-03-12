using System;
using System.Collections;
using System.IO;
using UnityEngine;
using System.Net.Http;
using System.Xml;

public class SaveLog : MonoBehaviour
{
    private float length;
    Queue queue;
    private void Awake()
    {
        DontDestroyOnLoad(this);
        Application.logMessageReceivedThreaded += OnReceiveLogMsg;
        queue = new Queue();
    }
    // Start is called before the first frame update

    void OnReceiveLogMsg(string condition, string stackTrace, LogType type)
    {
        string _type = "";
        switch (type)
        {
            case LogType.Error:
                _type = "error";
                break;
            case LogType.Assert:
                _type = "Assert";
                break;
            case LogType.Warning:
                _type = "Warning";
                break;
            case LogType.Log:
                _type = "Log";
                break;
            case LogType.Exception:
                _type = "Exception";
                break;
            default:
                break;
        }
        string msg = "[MSG]:" + condition + "--" + "[station]:" + stackTrace + "-" + "[LogType]:" + _type;
        queue.Enqueue(msg);
    }

    void Update()
    {
#if UNITY_EDITOR
#else
        CheckLogs();
#endif
    }
    void CheckLogs()
    {
        if (queue.Count != 0)
        {
            LogToFile(queue.Peek().ToString(), true, true, () => { queue.Dequeue(); });
        }
    }
    public void LogToFile(string str, bool bwithTime, bool bAppendLineFeed, System.Action callback = null)
    {
        if (str == null) return;
        try
        {
#if UNITY_EDITOR
            string fname = Application.dataPath + "/Unitylog_lby.txt";
#else
            string fname = Application.persistentDataPath+ "/Unitylog_lby.txt";
#endif


            if (fname == "" || fname == null) return;

            StreamWriter writer = new StreamWriter(fname, true, System.Text.Encoding.Default);

            if (bwithTime) writer.WriteLine("\r\n\r\n---------" + System.DateTime.Now.ToString());
            if (bAppendLineFeed) writer.WriteLine(str);
            else writer.Write(str);
            writer.Close();
            callback?.Invoke();
        }
        catch
        {

            throw;
        }
    }
}