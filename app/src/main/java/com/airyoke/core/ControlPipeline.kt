package com.airyoke.core

/**
 * 从传感器姿态到四轴输出的完整链路。
 *
 * ```
 * 旋转矩阵 → 校准（相对零点） → 取俯仰/滚转 → 轴映射（死区/曲线/增益/反转） → ControlOutput
 * ```
 *
 * 方向舵与油门来自屏幕滑条，不经这里 —— 它们由调用方直接填入 [ControlOutput]。
 * 把这条链路单独拎出来，是为了能在一个地方端到端地测，而不是散在 Activity 里。
 */
class ControlPipeline {

    val calibration = Calibration()

    /**
     * 副翼（来自绕设备 X 轴压盘）。
     *
     * 默认反转 —— 这两个默认值是**实机测出来的，不是猜的**（2026-09-27）：
     * 手机横握时"机头朝左"还是"朝右"会翻转符号，编译期无法确定。
     * 实测常见握姿下：右压盘得到负读数、前推盘得到正读数，与飞行语义相反，
     * 所以出厂默认两个轴都反转。换握姿就拨界面上的方向开关（会持久化）。
     */
    var aileronMapping = AxisMapping(inverted = true)

    /** 升降舵（来自绕设备 Y 轴推拉）。默认反转，理由同上。 */
    var elevatorMapping = AxisMapping(inverted = true)

    /** 最近一次算出的姿态角，供界面显示。 */
    var lastEuler: Attitude.Euler = Attitude.Euler(0f, 0f, 0f)
        private set

    /** 最近一次算出的归一化输出。 */
    var lastOutput: ControlOutput = ControlOutput(0f, 0f, 0f, 0.5f)
        private set

    /**
     * 处理一帧姿态。
     *
     * @param rotationMatrix 当前旋转矩阵（行主序 9 元素）。会被立即消费，不持有引用。
     * @param rudder 方向舵滑条值 −1..1
     * @param throttle 油门滑条值 −1..1
     */
    fun update(
        rotationMatrix: FloatArray,
        rudder: Float = 0f,
        throttle: Float = 0f,
    ): ControlOutput {
        val euler = calibration.eulerFor(rotationMatrix)
        val axes = Attitude.toControlAxes(euler)

        val output = ControlOutput(
            aileron = aileronMapping.apply(axes.aileronDeg),
            elevator = elevatorMapping.apply(axes.elevatorDeg),
            rudder = rudder.coerceIn(-1f, 1f),
            throttle = throttle.coerceIn(-1f, 1f),
        )

        lastEuler = euler
        lastOutput = output
        return output
    }

    /** 以当前姿态作为新的零点（对应「校准水平姿态」与「飞行中微调」）。 */
    fun zeroHere(rotationMatrix: FloatArray) = calibration.setReference(rotationMatrix)
}
