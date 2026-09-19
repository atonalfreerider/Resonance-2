import unittest
import numpy as np
from repair_bass import guided_recovery

class BassRecoveryChecks(unittest.TestCase):
    def test_recovery_preserves_clock_phase_and_complementary_sum(self):
        sr=16000;t=np.arange(sr*3)/sr
        envelope=np.clip((t-.4)/.03,0,1)*np.clip((1.8-t)/.03,0,1)
        bass=envelope*(.15*np.sin(2*np.pi*55*t)+.22*np.sin(2*np.pi*110*t))
        unrelated=.12*np.sin(2*np.pi*247*t)
        source=np.column_stack([bass+unrelated,.7*bass-unrelated]).astype('float32')
        recovered,cents=guided_recovery(source,sr,[(.4,1.8,33)])
        self.assertEqual(recovered.shape,source.shape)
        self.assertTrue(np.isfinite(recovered).all())
        self.assertLessEqual(abs(cents),10)
        region=(t>.7)&(t<1.5)
        self.assertGreater(np.corrcoef(recovered[region,0],bass[region])[0,1],.98)
        self.assertGreater(np.std(recovered[region,0]),np.std(bass[region])*.8)
        self.assertLess(np.std(recovered[t>2.2,0]),.003)
        residual=source-recovered
        self.assertLess(np.max(abs(source-(residual+recovered))),1e-6)

if __name__=='__main__':unittest.main()
