using System;
using UnityEngine;

namespace Tanks
{
	/// <summary>
	/// Сеттинги трансмиссии двигателя танка
	/// </summary>
	[CreateAssetMenu(menuName = "Settings/Transmission", fileName = "Transmission", order = 0)]
	public class TransmissionSettings : ScriptableObject
	{
		private const float c_errorEngine = .5f;
		private const float c_errorTorque = 100f;
		
		[SerializeField, Tooltip("Графики зависимости мощности от скорости по передачам")]
		private AnimationCurve[] _data;

		/// <summary>
		/// Расчет скорости вращения двигателя на определенной физскорости
		/// </summary>
		/// <param name="speed">Скорость перемещения</param>
		/// <returns>[0, 1] - раскрутка ротора на текущей передачи</returns>
		public float EngineSpeed(float speed)
		{
			if (FindData(speed, out var curve))
			{
				// защититься, если в кривой всего один ключ — InverseLerp корректно вернёт 0 при равных значениях
				var keys = curve?.keys;
				if (keys == null || keys.Length == 0) return c_errorEngine;
				var first = keys[0].time;
				var last = keys[^1].time;
				return Mathf.InverseLerp(first, last, speed);
			}
			return c_errorEngine;
		}
		
		/// <summary>
		/// Получить крутящий момент
		/// </summary>
		/// <param name="speed">Скорость танка</param>
		/// <returns>Крутящий момент, выдаваемый двигателем</returns>
		public float GetTorque(float speed)
			=> FindData(speed, out var curve)
				? curve.Evaluate(speed)
				: c_errorTorque;

        private bool FindData(float speed, out AnimationCurve curve)
        {
            return FindData(speed, out curve, $"Incorrect <b>{nameof(TransmissionSettings)}</b> configuration. Wrong request: <b>{speed}</b>");
        }

        private bool FindData(float speed, out AnimationCurve curve, string logMessage)
		{
			// Если ассет не заполнен — создаём временную запасную кривую в рантайме,
			// чтобы танк мог ехать и чтобы не засорять консоль критическими ошибками.
			if (_data == null || _data.Length == 0)
			{
				Debug.LogWarning($"TransmissionSettings: _data is null or empty. Creating default runtime curve. {logMessage}", this);
				_data = new[]
				{
					// простая кривая: высокий крутящий момент на низких скоростях, снижаясь к высоким
					new AnimationCurve(
						new Keyframe(0f, 400f),
						new Keyframe(50f, 200f),
						new Keyframe(150f, 0f)
					)
				};
			}

			bool compare(AnimationCurve c, float value)
			{
				if (c == null) return false;
				var keys = c.keys;
				if (keys == null || keys.Length == 0) return false;
				// допустимая область — от первого до последнего ключа
				return value >= keys[0].time && value <= keys[^1].time;
			}
			
			var index = Array.FindIndex(_data, t => compare(t, speed));
			if (index == -1)
			{
				Debug.LogError(logMessage, this);	
				curve = null;
				return false;
			}

			curve = _data[index];
			return true;
		}
	}	
}