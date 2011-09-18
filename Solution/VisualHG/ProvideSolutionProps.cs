using System;
using System.Collections.Generic;
using System.Text;
using System.Globalization;
using Microsoft.VisualStudio.Shell;

namespace VisualHG
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    internal sealed class ProvideSolutionProps : RegistrationAttribute
    {
        private string _propName;

        public ProvideSolutionProps(string propName)
        {
            _propName = propName;
        }

        public override void Register(RegistrationContext context)
        {
            context.Log.WriteLine(string.Format(CultureInfo.InvariantCulture, "ProvideSolutionProps: ({0} = {1})", context.ComponentType.GUID.ToString("B"), PropName));

            Key childKey = null;

            try
            {
                childKey = context.CreateKey(string.Format(CultureInfo.InvariantCulture, "{0}\\{1}", "SolutionPersistence", PropName));

                childKey.SetValue(string.Empty, context.ComponentType.GUID.ToString("B"));
            }
            finally
            {
                if (childKey != null) childKey.Close();
            }
        }

        public override void Unregister(RegistrationContext context)
        {
            context.RemoveKey(string.Format(CultureInfo.InvariantCulture, "{0}\\{1}", "SolutionPersistence", PropName));
        }

        public string PropName { get { return _propName; } }
    }
}
