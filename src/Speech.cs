using System;
using System.Reflection;

namespace WinShutDown
{
    /// <summary>
    /// Озвучивание предупреждений встроенным в Windows синтезом речи (SAPI 5), русским голосом.
    /// Ищет голос сначала среди классических (Microsoft Irina Desktop), затем среди голосов Windows 10/11
    /// (Microsoft Irina, Microsoft Pavel), которые добавляются в «Параметры → Время и язык → Речь».
    /// </summary>
    internal sealed class Speaker
    {
        const int SVSFlagsAsync = 1, SVSFPurgeBeforeSpeak = 2;

        static readonly string[] Categories =
        {
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\Voices",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech_OneCore\Voices",
        };

        readonly object voice; // SAPI.SpVoice

        public string VoiceName { get; }

        Speaker(object voice, string name)
        {
            this.voice = voice;
            VoiceName = name;
        }

        /// <summary>null — русского голоса нет (или синтез речи недоступен).</summary>
        public static Speaker TryCreate()
        {
            try
            {
                Type voiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
                Type categoryType = Type.GetTypeFromProgID("SAPI.SpObjectTokenCategory");
                if (voiceType == null || categoryType == null) return null;

                foreach (string category in Categories)
                {
                    try
                    {
                        object cat = Activator.CreateInstance(categoryType);
                        Call(cat, "SetId", category, false);
                        object tokens = Call(cat, "EnumerateTokens", "Language=419", ""); // 419 — русский
                        if ((int)Get(tokens, "Count") == 0) continue;
                        object token = Call(tokens, "Item", 0);
                        string name = (string)Call(token, "GetDescription", 0);

                        object voice = Activator.CreateInstance(voiceType);
                        voice.GetType().InvokeMember("Voice", BindingFlags.PutRefDispProperty, null, voice, new[] { token });
                        voice.GetType().InvokeMember("Volume", BindingFlags.SetProperty, null, voice, new object[] { 100 });
                        return new Speaker(voice, name);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Голос из " + category + " недоступен", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Синтез речи недоступен", ex);
            }
            return null;
        }

        /// <summary>Произнести фразу (не дожидаясь окончания; предыдущая фраза прерывается).</summary>
        public bool Say(string text)
        {
            try
            {
                Call(voice, "Speak", text, SVSFlagsAsync | SVSFPurgeBeforeSpeak);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Ошибка синтеза речи", ex);
                return false;
            }
        }

        static object Call(object target, string method, params object[] args) =>
            target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args);

        static object Get(object target, string property) =>
            target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null);
    }
}
