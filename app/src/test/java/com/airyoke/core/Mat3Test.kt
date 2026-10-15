package com.airyoke.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class Mat3Test {

    @Test
    fun `单位矩阵乘以任何矩阵等于其自身`() {
        val r = RotationFixtures.rotY(37f)
        val product = Mat3.multiply(Mat3.identity(), r)
        for (i in 0 until Mat3.SIZE) {
            assertEquals("索引 $i", r[i], product[i], 1e-5f)
        }
    }

    @Test
    fun `转置两次回到原矩阵`() {
        val r = RotationFixtures.compose(RotationFixtures.rotZ(20f), RotationFixtures.rotX(35f))
        val back = Mat3.transpose(Mat3.transpose(r))
        for (i in 0 until Mat3.SIZE) {
            assertEquals("索引 $i", r[i], back[i], 1e-5f)
        }
    }

    @Test
    fun `转置支持原地操作`() {
        val r = RotationFixtures.rotY(15f)
        val expected = Mat3.transpose(r.copyOf())
        val inPlace = r.copyOf()
        Mat3.transpose(inPlace, inPlace)
        for (i in 0 until Mat3.SIZE) {
            assertEquals("索引 $i", expected[i], inPlace[i], 1e-6f)
        }
    }

    @Test
    fun `旋转矩阵乘以自身的转置等于单位矩阵`() {
        // 旋转矩阵是正交矩阵，这条性质是"转置即逆"的依据
        val r = RotationFixtures.compose(
            RotationFixtures.rotZ(25f),
            RotationFixtures.rotX(40f),
            RotationFixtures.rotY(10f),
        )
        val shouldBeIdentity = Mat3.multiply(r, Mat3.transpose(r))
        assertTrue("应接近单位矩阵", Mat3.isIdentity(shouldBeIdentity, eps = 1e-4f))
    }

    @Test
    fun `isIdentity 能识别非单位矩阵`() {
        assertFalse(Mat3.isIdentity(RotationFixtures.rotX(1f)))
    }

    @Test
    fun `multiply 允许结果数组与输入数组相同`() {
        val a = RotationFixtures.rotX(10f)
        val b = RotationFixtures.rotY(20f)
        val expected = Mat3.multiply(a, b)
        val aliased = a.copyOf()
        Mat3.multiply(aliased, b, aliased)
        for (i in 0 until Mat3.SIZE) {
            assertEquals("索引 $i", expected[i], aliased[i], 1e-6f)
        }
    }
}
