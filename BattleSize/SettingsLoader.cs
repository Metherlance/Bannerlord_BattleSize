using System.Xml;
using System.Xml.Serialization;

namespace BattleSize
{
    public static class SettingsLoader
    {
        public static Settings LoadSettings(string filepath)
        {
            Settings settings;
            using (XmlReader xmlReader = XmlReader.Create(filepath))
            {
                int content = (int)xmlReader.MoveToContent();
                settings = (Settings)new XmlSerializer(typeof(Settings), new XmlRootAttribute()
                {
                    ElementName = xmlReader.Name
                }).Deserialize(xmlReader);
            }
            return settings;
        }
    }
}
