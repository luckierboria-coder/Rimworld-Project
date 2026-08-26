$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'Source\LovinAnywhere.cs'
$text = Get-Content -LiteralPath $path -Raw

$old = @'
        private static object[] BuildArguments(MethodInfo method, Job job)
        {
            ParameterInfo[] ps = method.GetParameters();
            object[] args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                Type t = ps[i].ParameterType;
                if (i == 0 && t == typeof(Job))
                    args[i] = job;
                else if (t == typeof(JobCondition))
                    args[i] = JobCondition.InterruptForced;
                else if (ps[i].HasDefaultValue)
                    args[i] = ps[i].DefaultValue;
                else if (t == typeof(bool))
                    args[i] = false;
                else if (t.IsValueType)
                    args[i] = Activator.CreateInstance(t);
                else
                    args[i] = null;
            }
            return args;
        }
'@

$new = @'
        private static object[] BuildArguments(MethodInfo method, Job job)
        {
            ParameterInfo[] ps = method.GetParameters();
            object[] args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                Type t = ps[i].ParameterType;
                if (i == 0 && t == typeof(Job))
                {
                    args[i] = job;
                    continue;
                }
                if (t == typeof(JobCondition))
                {
                    args[i] = JobCondition.InterruptForced;
                    continue;
                }

                // Mono can expose optional Nullable<T> defaults using the underlying
                // metadata primitive (for example Byte for JobTag?). MethodInfo.Invoke
                // will not convert that primitive back into Nullable<T>, so all optional
                // nullable StartJob/TryTakeOrderedJob arguments must be supplied as null.
                if (Nullable.GetUnderlyingType(t) != null)
                {
                    args[i] = null;
                    continue;
                }

                if (ps[i].HasDefaultValue && IsCompatibleDefaultValue(t, ps[i].DefaultValue))
                {
                    args[i] = ps[i].DefaultValue;
                    continue;
                }
                if (t == typeof(bool))
                    args[i] = false;
                else if (t.IsValueType)
                    args[i] = Activator.CreateInstance(t);
                else
                    args[i] = null;
            }
            return args;
        }

        private static bool IsCompatibleDefaultValue(Type parameterType, object value)
        {
            if (value == null || value == DBNull.Value || value == Type.Missing)
                return !parameterType.IsValueType;
            return parameterType.IsInstanceOfType(value);
        }
'@

if ($text.Contains($new)) {
    Write-Host 'v4 JobStarter fix already present.'
    exit 0
}
if (!$text.Contains($old)) {
    throw 'Expected v3 BuildArguments block was not found; source changed and needs manual review.'
}
$text = $text.Replace($old, $new)
Set-Content -LiteralPath $path -Value $text -Encoding UTF8
Write-Host 'Applied MBL v4 nullable StartJob argument fix.'
