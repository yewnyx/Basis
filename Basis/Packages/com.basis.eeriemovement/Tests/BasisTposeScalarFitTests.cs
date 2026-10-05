using Basis.IK;
using NUnit.Framework;
using UnityEngine;
namespace Basis.Tests.IK
{
    public class BasisTposeScalarFitTests
    {
        const float tolerance = 1e-5f;
        static BasisEerieMovement Baked()
        {
            var job = new BasisEerieMovement
            {
                tposeBakeScale = 1f,
                tposeArmFitScale = 1f,
                tposeTorsoFitScale = 1f,
                tposeClavicleLenLeft = 0.1f,
                tposeClavicleLenRight = 0.12f,
                tposeShoulderToHandLeft = 0.6f,
                tposeShoulderToHandRight = 0.62f,
                tposeShoulderToElbowLeft = 0.35f,
                tposeShoulderToElbowRight = 0.37f,
                tposeLengthNeckToHips = new Vector3(0f, 0.5f, 0f),
                tposeHeadToNeckLocal = new Vector3(0f, -0.1f, 0f),
                minHeadSpineHeight = 0.62f,
            };
            job.SpineData.positions.Length = 3;
            job.SpineData.restPositions.Length = 3;
            job.SpineData.positions[0] = job.SpineData.restPositions[0] = new Vector3(1f, 2f, 3f);
            job.SpineData.positions[1] = job.SpineData.restPositions[1] = new Vector3(1f, 2.25f, 3f);
            job.SpineData.positions[2] = job.SpineData.restPositions[2] = new Vector3(1f, 2.75f, 3f);
            job.SpineData.length = 0.5f;
            return job;
        }
        [Test]
        public void ArmFit_ScalesTheArmBeyondTheClavicle()
        {
            var job = Baked();
            job.RescaleTposeFit(1.2f, 1f);
            Assert.AreEqual(0.1f, job.tposeClavicleLenLeft, tolerance);
            Assert.AreEqual(0.1f + 0.5f * 1.2f, job.tposeShoulderToHandLeft, tolerance);
            Assert.AreEqual(0.1f + 0.25f * 1.2f, job.tposeShoulderToElbowLeft, tolerance);
            Assert.AreEqual(0.12f + 0.5f * 1.2f, job.tposeShoulderToHandRight, tolerance);
            Assert.AreEqual(0.5f, job.tposeLengthNeckToHips.y, tolerance);
            Assert.AreEqual(0.62f, job.minHeadSpineHeight, tolerance);
        }
        [Test]
        public void TorsoFit_ScalesTheSpineScalars()
        {
            var job = Baked();
            job.RescaleTposeFit(1f, 0.9f);
            Assert.AreEqual(0.45f, job.tposeLengthNeckToHips.y, tolerance);
            Assert.AreEqual(-0.09f, job.tposeHeadToNeckLocal.y, tolerance);
            Assert.AreEqual(0.62f * 0.9f, job.minHeadSpineHeight, tolerance);
            Assert.AreEqual(0.6f, job.tposeShoulderToHandLeft, tolerance);
        }
        [Test]
        public void Refit_IsIdempotentAndReversible()
        {
            var job = Baked();
            job.RescaleTposeFit(1.2f, 0.9f);
            var once = job;
            job.RescaleTposeFit(1.2f, 0.9f);
            Assert.AreEqual(once.tposeShoulderToHandLeft, job.tposeShoulderToHandLeft, tolerance);
            Assert.AreEqual(once.minHeadSpineHeight, job.minHeadSpineHeight, tolerance);
            job.RescaleTposeFit(1f, 1f);
            var baked = Baked();
            Assert.AreEqual(baked.tposeShoulderToHandLeft, job.tposeShoulderToHandLeft, tolerance);
            Assert.AreEqual(baked.tposeShoulderToElbowRight, job.tposeShoulderToElbowRight, tolerance);
            Assert.AreEqual(baked.tposeLengthNeckToHips.y, job.tposeLengthNeckToHips.y, tolerance);
            Assert.AreEqual(baked.minHeadSpineHeight, job.minHeadSpineHeight, tolerance);
        }
        [Test]
        public void UniformRescale_ComposesWithTheFit()
        {
            var job = Baked();
            job.RescaleTposeFit(1.2f, 0.9f);
            job.RescaleTposeScalars(2f);
            Assert.AreEqual(2f * (0.1f + 0.5f * 1.2f), job.tposeShoulderToHandLeft, tolerance);
            Assert.AreEqual(0.2f, job.tposeClavicleLenLeft, tolerance);
            Assert.AreEqual(0.9f, job.tposeLengthNeckToHips.y, tolerance);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), job.SpineData.restPositions[0]);
            Assert.AreEqual(new Vector3(1f, 2.5f, 3f), job.SpineData.restPositions[1]);
            Assert.AreEqual(new Vector3(1f, 3.5f, 3f), job.SpineData.restPositions[2]);
            Assert.AreEqual(job.SpineData.restPositions[2], job.SpineData.positions[2]);
            Assert.AreEqual(1f, job.SpineData.length, tolerance);
            job.RescaleTposeFit(1f, 1f);
            Assert.AreEqual(1.2f, job.tposeShoulderToHandLeft, tolerance);
            Assert.AreEqual(1f, job.tposeLengthNeckToHips.y, tolerance);
            Assert.AreEqual(new Vector3(1f, 2.75f, 3f), job.SpineData.restPositions[2]);
            Assert.AreEqual(0.5f, job.SpineData.length, tolerance);
        }
        [Test]
        public void InvalidScales_AreIgnored()
        {
            var job = Baked();
            job.RescaleTposeFit(0f, float.NaN);
            Assert.AreEqual(0.6f, job.tposeShoulderToHandLeft, tolerance);
            Assert.AreEqual(1f, job.tposeArmFitScale, tolerance);
        }
    }
}
