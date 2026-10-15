package com.airyoke.model

import android.content.Context
import androidx.core.content.edit
import com.airyoke.core.Mat3

/** 保存体感零点及方向设置。油门卡位使用主界面的独立机型设置。 */
class CalibrationStore(context: Context) {
    private val prefs = context.getSharedPreferences("airyoke.settings", Context.MODE_PRIVATE)
    fun loadCalibration(): FloatArray? {
        val parts = prefs.getString("calibration.reference", null)?.split(',') ?: return null
        if (parts.size != Mat3.SIZE) return null
        val values = FloatArray(Mat3.SIZE)
        for (i in parts.indices) {
            values[i] = parts[i].toFloatOrNull()?.takeIf { it.isFinite() } ?: return null
        }
        return values
    }
    fun saveCalibration(reference: FloatArray?) {
        prefs.edit {
            if (reference == null || reference.size != Mat3.SIZE) remove("calibration.reference")
            else putString("calibration.reference", reference.joinToString(","))
        }
    }
    fun loadInvertAileron() = prefs.getBoolean("axis.aileron.inverted", true)
    fun saveInvertAileron(value: Boolean) = prefs.edit { putBoolean("axis.aileron.inverted", value) }
    fun loadInvertElevator() = prefs.getBoolean("axis.elevator.inverted", true)
    fun saveInvertElevator(value: Boolean) = prefs.edit { putBoolean("axis.elevator.inverted", value) }
    fun loadYokeEngaged() = prefs.getBoolean("yoke.engaged", true)
    fun saveYokeEngaged(value: Boolean) = prefs.edit { putBoolean("yoke.engaged", value) }
}
